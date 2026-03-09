using System.Text.Json;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Helpers;
using NArk.Core.Models;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Core.Services;

/// <summary>
/// Result of a delegation operation.
/// </summary>
public record DelegationResult(
    string[] DelegatedOutpoints,
    string[] FailedOutpoints);

/// <summary>
/// Orchestrates VTXO delegation to an external delegator service.
/// Builds intent proofs and partial forfeit transactions, then submits them
/// to the delegator which will renew the VTXOs on the user's behalf.
/// </summary>
public class DelegationService(
    IEnumerable<IDelegationTransformer> transformers,
    IDelegatorProvider delegatorProvider,
    IClientTransport clientTransport,
    IContractStorage contractStorage,
    IWalletProvider walletProvider,
    ILogger<DelegationService>? logger = null)
{
    /// <summary>
    /// Delegates the given VTXOs to the configured delegator service.
    /// The delegator will renew them in a future batch round on the user's behalf.
    /// </summary>
    /// <param name="walletIdentifier">The wallet that owns the VTXOs.</param>
    /// <param name="vtxos">VTXOs to delegate.</param>
    /// <param name="destination">Ark address where renewed funds should be sent.</param>
    /// <param name="delegateAt">Optional: when the delegation should activate (defaults to now).</param>
    /// <param name="rejectReplace">If true, reject if the delegator already has a pending delegation for overlapping VTXOs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<DelegationResult> DelegateAsync(
        string walletIdentifier,
        IReadOnlyList<ArkVtxo> vtxos,
        ArkAddress destination,
        DateTimeOffset? delegateAt = null,
        bool rejectReplace = false,
        CancellationToken cancellationToken = default)
    {
        var delegateInfo = await delegatorProvider.GetDelegateInfoAsync(cancellationToken);
        logger?.LogInformation("Delegator info: pubkey={Pubkey}, fee={Fee}, address={Address}",
            delegateInfo.Pubkey, delegateInfo.Fee, delegateInfo.DelegatorAddress);

        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);

        // Transform VTXOs into delegation-ready coins
        var coins = new List<ArkCoin>();
        var failed = new List<string>();

        foreach (var vtxo in vtxos)
        {
            var contracts = await contractStorage.GetContracts(
                walletIds: [walletIdentifier],
                scripts: [vtxo.Script],
                cancellationToken: cancellationToken);

            var contractEntity = contracts.FirstOrDefault();
            if (contractEntity is null)
            {
                logger?.LogWarning("No contract found for VTXO {Outpoint}", vtxo.OutPoint);
                failed.Add(vtxo.OutPoint.ToString());
                continue;
            }

            var contract = ArkContractParser.Parse(contractEntity.Type, contractEntity.AdditionalData, serverInfo.Network);
            if (contract is null)
            {
                logger?.LogWarning("Failed to parse contract for VTXO {Outpoint}", vtxo.OutPoint);
                failed.Add(vtxo.OutPoint.ToString());
                continue;
            }

            ArkCoin? coin = null;
            foreach (var transformer in transformers)
            {
                if (await transformer.CanDelegate(walletIdentifier, contract, vtxo, delegateInfo))
                {
                    coin = await transformer.Transform(walletIdentifier, contract, vtxo, delegateInfo);
                    break;
                }
            }

            if (coin is null)
            {
                logger?.LogWarning("No delegation transformer matched VTXO {Outpoint}", vtxo.OutPoint);
                failed.Add(vtxo.OutPoint.ToString());
                continue;
            }

            coins.Add(coin);
        }

        if (coins.Count == 0)
        {
            logger?.LogWarning("No VTXOs could be delegated");
            return new DelegationResult([], failed.ToArray());
        }

        // Build the intent message
        var delegatorPubkey = ECPubKey.Create(Convert.FromHexString(delegateInfo.Pubkey));
        var signer = await walletProvider.GetSignerAsync(walletIdentifier, cancellationToken)
                     ?? throw new InvalidOperationException("No signer found for wallet");
        var addrProvider = await walletProvider.GetAddressProviderAsync(walletIdentifier, cancellationToken)
                           ?? throw new InvalidOperationException("No address provider found for wallet");
        var signingDescriptor = await addrProvider.GetNextSigningDescriptor(cancellationToken);
        var signerPubKey = await signer.GetPubKey(signingDescriptor, cancellationToken);

        var validAt = delegateAt ?? DateTimeOffset.UtcNow;
        var expireAt = DateTimeOffset.UtcNow.AddMinutes(10);

        // Build outputs: destination + fee to delegator (if fee > 0)
        var outputsList = new List<TxOut>
        {
            new(coins.Sum(c => c.Amount) - Money.Satoshis((long)(delegateInfo.Fee * (ulong)coins.Count)),
                destination.ScriptPubKey)
        };

        if (delegateInfo.Fee > 0 && !string.IsNullOrEmpty(delegateInfo.DelegatorAddress))
        {
            var delegatorArkAddress = ArkAddress.Parse(delegateInfo.DelegatorAddress);
            outputsList.Add(new TxOut(
                Money.Satoshis((long)(delegateInfo.Fee * (ulong)coins.Count)),
                delegatorArkAddress.ScriptPubKey));
        }

        var msg = new Messages.RegisterIntentMessage
        {
            Type = "register",
            OnchainOutputsIndexes = [],
            ValidAt = validAt.ToUnixTimeSeconds(),
            ExpireAt = expireAt.ToUnixTimeSeconds(),
            CosignersPublicKeys = [delegateInfo.Pubkey]
        };
        var intentMessage = JsonSerializer.Serialize(msg);

        // Build intent proof PSBT (BIP-322 style)
        var intentProof = await BuildIntentProof(
            intentMessage, serverInfo.Network, coins.ToArray(), outputsList, signer, cancellationToken);

        // Build partial forfeit PSBTs (one per input, ANYONECANPAY|ALL, no connector)
        var txBuilder = new TransactionHelpers.ArkTransactionBuilder(
            clientTransport, null!, walletProvider, null!);

        var forfeitTxs = new List<string>();
        foreach (var coin in coins)
        {
            var forfeit = await txBuilder.ConstructForfeitTx(
                serverInfo, coin, null, serverInfo.ForfeitAddress, cancellationToken);
            forfeitTxs.Add(forfeit.ToBase64());
        }

        // Submit to delegator
        await delegatorProvider.DelegateAsync(
            intentMessage,
            intentProof.ToBase64(),
            forfeitTxs.ToArray(),
            rejectReplace,
            cancellationToken);

        logger?.LogInformation("Delegated {Count} VTXOs to delegator", coins.Count);

        return new DelegationResult(
            coins.Select(c => c.Outpoint.ToString()).ToArray(),
            failed.ToArray());
    }

    private async Task<PSBT> BuildIntentProof(
        string message,
        Network network,
        ArkCoin[] inputs,
        List<TxOut> outputs,
        IArkadeWalletSigner signer,
        CancellationToken cancellationToken)
    {
        var firstInput = inputs.First();
        var maxLockTime = inputs
            .Where(c => c.LockTime is not null)
            .Select(c => (uint)c.LockTime!.Value)
            .DefaultIfEmpty(0U)
            .Max();

        var messageHash = HashHelpers.CreateTaggedMessageHash("ark-intent-proof-message", message);

        // BIP-322 toSpend transaction
        var toSpend = network.CreateTransaction();
        toSpend.Version = 0;
        toSpend.LockTime = 0;
        toSpend.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, 0xFFFFFFFF),
            new Script(OpcodeType.OP_0, Op.GetPushOp(messageHash)))
        {
            Sequence = 0,
            WitScript = WitScript.Empty,
        });
        toSpend.Outputs.Add(new TxOut(Money.Zero, firstInput.ScriptPubKey));

        // toSign transaction
        var toSign = network.CreateTransaction();
        toSign.Version = 2;
        toSign.LockTime = maxLockTime;

        // First input: BIP-322 reference
        toSign.Inputs.Add(new TxIn(new OutPoint(toSpend.GetHash(), 0)) { Sequence = 0 });

        // VTXO inputs
        foreach (var input in inputs)
        {
            toSign.Inputs.Add(new TxIn(input.Outpoint) { Sequence = 0 });
        }

        // Outputs
        foreach (var output in outputs)
        {
            toSign.Outputs.Add(output);
        }

        var psbt = PSBT.FromTransaction(toSign, network);
        psbt.Settings.AutomaticUTXOTrimming = false;
        psbt.AddTransactions(toSpend);
        psbt.AddCoins(inputs.Cast<ICoin>().ToArray());

        // Build coin array for precomputed data: first is BIP-322 toSpend output, then VTXO inputs
        var allInputCoins = new[] { new ArkCoin(firstInput) }
            .Concat(inputs)
            .ToArray();
        allInputCoins[0].TxOut = psbt.Inputs[0].GetTxOut();
        allInputCoins[0].Outpoint = psbt.Inputs[0].PrevOut;

        var gtx = psbt.GetGlobalTransaction();
        var precomputedData = gtx.PrecomputeTransactionData(
            allInputCoins.Select(c => c.TxOut).ToArray());

        psbt = PSBT.FromTransaction(gtx, network).UpdateFrom(psbt);

        // Sign each input
        foreach (var coin in allInputCoins)
        {
            await PsbtHelpers.SignAndFillPsbt(signer, coin, psbt, precomputedData,
                cancellationToken: cancellationToken);
        }

        return psbt;
    }
}
