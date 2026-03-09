using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Core.Transformers;

public class DelegateContractDelegationTransformer(
    IWalletProvider walletProvider,
    ILogger<DelegateContractDelegationTransformer>? logger = null) : IDelegationTransformer
{
    public async Task<bool> CanDelegate(string walletIdentifier, ArkContract contract, ArkVtxo vtxo, DelegateInfo delegator)
    {
        if (contract is not ArkDelegateContract delegateContract)
            return false;

        // Verify the delegator's pubkey matches the contract's delegate key
        var delegatorPubkey = ECXOnlyPubKey.Create(Convert.FromHexString(delegator.Pubkey));
        if (!delegateContract.Delegate.ToXOnlyPubKey().Equals(delegatorPubkey))
        {
            logger?.LogDebug(
                "Delegator pubkey mismatch: contract={ContractDelegate}, delegator={DelegatorPubkey}",
                delegateContract.Delegate, delegator.Pubkey);
            return false;
        }

        if (await walletProvider.GetAddressProviderAsync(walletIdentifier) is not { } addressProvider)
            return false;

        if (!await addressProvider.IsOurs(delegateContract.User))
            return false;

        if (await walletProvider.GetSignerAsync(walletIdentifier) is null)
            return false;

        return true;
    }

    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo, DelegateInfo delegator)
    {
        var delegateContract = (ArkDelegateContract)contract;

        return new ArkCoin(
            walletIdentifier,
            contract,
            vtxo.CreatedAt,
            vtxo.ExpiresAt,
            vtxo.ExpiresAtHeight,
            vtxo.OutPoint,
            vtxo.TxOut,
            delegateContract.User,
            delegateContract.DelegatePath(),
            null,
            delegateContract.CltvLocktime,
            null,
            vtxo.Swept,
            vtxo.Unrolled);
    }
}
