using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Core.Contracts;
using NArk.Core.Transformers;
using NArk.Core.Transport;

namespace NArk.Core.Services;

/// <summary>
/// Orchestrates VTXO delegation to an external delegator service (e.g. Fulmine).
/// Registers Ark addresses for automatic rollover — the delegator monitors them and
/// participates in batch rounds on the owner's behalf before VTXOs expire.
/// </summary>
public class DelegationService(
    IEnumerable<IDelegationTransformer> transformers,
    IDelegatorProvider delegatorProvider,
    IClientTransport clientTransport,
    IContractStorage contractStorage,
    ILogger<DelegationService>? logger = null)
{
    /// <summary>
    /// Returns the delegator's compressed public key (hex).
    /// Use this when constructing <see cref="ArkDelegateContract"/> scripts.
    /// </summary>
    public Task<string> GetDelegatePublicKeyAsync(CancellationToken cancellationToken = default)
        => delegatorProvider.GetDelegatePublicKeyAsync(cancellationToken);

    /// <summary>
    /// Registers the given VTXOs with the delegator for automatic rollover.
    /// Each VTXO's contract must match a registered <see cref="IDelegationTransformer"/>.
    /// </summary>
    /// <param name="walletIdentifier">The wallet that owns the VTXOs.</param>
    /// <param name="vtxos">VTXOs to watch for rollover.</param>
    /// <param name="destinationAddress">Ark address where rolled-over funds should be sent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Addresses successfully watched and outpoints that failed.</returns>
    public async Task<WatchResult> WatchForRolloverAsync(
        string walletIdentifier,
        IReadOnlyList<ArkVtxo> vtxos,
        string destinationAddress,
        CancellationToken cancellationToken = default)
    {
        var delegatePubkey = await delegatorProvider.GetDelegatePublicKeyAsync(cancellationToken);
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);

        var watched = new List<string>();
        var failed = new List<string>();

        // Group VTXOs by script to avoid duplicate watches for the same address
        foreach (var group in vtxos.GroupBy(v => v.Script))
        {
            var script = group.Key;

            var contracts = await contractStorage.GetContracts(
                walletIds: [walletIdentifier],
                scripts: [script],
                cancellationToken: cancellationToken);

            var contractEntity = contracts.FirstOrDefault();
            if (contractEntity is null)
            {
                logger?.LogWarning("No contract found for script {Script}", script);
                foreach (var v in group) failed.Add(v.OutPoint.ToString());
                continue;
            }

            var contract = ArkContractParser.Parse(contractEntity.Type, contractEntity.AdditionalData, serverInfo.Network);
            if (contract is null)
            {
                logger?.LogWarning("Failed to parse contract for script {Script}", script);
                foreach (var v in group) failed.Add(v.OutPoint.ToString());
                continue;
            }

            // Check if any transformer recognises this contract as delegatable
            var canDelegate = false;
            foreach (var transformer in transformers)
            {
                if (await transformer.CanDelegate(walletIdentifier, contract, delegatePubkey))
                {
                    canDelegate = true;
                    break;
                }
            }

            if (!canDelegate)
            {
                logger?.LogWarning("No delegation transformer matched contract type {Type} for script {Script}",
                    contract.Type, script);
                foreach (var v in group) failed.Add(v.OutPoint.ToString());
                continue;
            }

            // Extract tapscripts as hex strings
            var tapscripts = contract.GetTapScriptList()
                .Select(ts => ts.Script.ToHex())
                .ToArray();

            var arkAddress = contract.GetArkAddress().ToString(false);

            await delegatorProvider.WatchAddressForRolloverAsync(
                arkAddress, tapscripts, destinationAddress, cancellationToken);

            logger?.LogInformation("Watching address {Address} for rollover", arkAddress);
            watched.Add(arkAddress);
        }

        return new WatchResult(watched.ToArray(), failed.ToArray());
    }

    /// <summary>
    /// Stops watching an Ark address for rollover.
    /// </summary>
    public async Task UnwatchAsync(string address, CancellationToken cancellationToken = default)
    {
        await delegatorProvider.UnwatchAddressAsync(address, cancellationToken);
        logger?.LogInformation("Unwatched address {Address}", address);
    }

    /// <summary>
    /// Lists all addresses currently watched by the delegator.
    /// </summary>
    public Task<IReadOnlyList<WatchedRolloverAddress>> ListWatchedAsync(CancellationToken cancellationToken = default)
        => delegatorProvider.ListWatchedAddressesAsync(cancellationToken);
}

/// <summary>
/// Result of a watch-for-rollover operation.
/// </summary>
/// <param name="WatchedAddresses">Ark addresses successfully registered for rollover.</param>
/// <param name="FailedOutpoints">VTXO outpoints that could not be registered.</param>
public record WatchResult(string[] WatchedAddresses, string[] FailedOutpoints);
