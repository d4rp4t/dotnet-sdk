using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Wallet;

/// <summary>
/// Wraps an existing <see cref="IArkadeAddressProvider"/> to override contract derivation
/// for Receive and SendToSelf purposes, producing <see cref="ArkDelegateContract"/>
/// instead of <see cref="ArkPaymentContract"/>.
/// Boarding contracts pass through unchanged.
/// </summary>
public class DelegatingAddressProvider(
    IArkadeAddressProvider inner,
    OutputDescriptor delegateKey,
    OutputDescriptor serverKey,
    Sequence exitDelay,
    LockTime? cltvLocktime = null) : IArkadeAddressProvider
{
    public Task<bool> IsOurs(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
        => inner.IsOurs(descriptor, cancellationToken);

    public Task<OutputDescriptor> GetNextSigningDescriptor(CancellationToken cancellationToken = default)
        => inner.GetNextSigningDescriptor(cancellationToken);

    public async Task<(ArkContract contract, ArkContractEntity entity)> GetNextContract(
        NextContractPurpose purpose,
        ContractActivityState activityState,
        ArkContract[]? inputContracts = null,
        CancellationToken cancellationToken = default)
    {
        // Boarding contracts are never delegated
        if (purpose == NextContractPurpose.Boarding)
            return await inner.GetNextContract(purpose, activityState, inputContracts, cancellationToken);

        // Call inner to get the signing descriptor allocation and entity with correct wallet ID
        var (innerContract, innerEntity) = await inner.GetNextContract(purpose, activityState, inputContracts, cancellationToken);

        // Only override ArkPaymentContract — other types (UnknownArkContract for sweep,
        // or recycled descriptors) pass through unchanged
        if (innerContract is not ArkPaymentContract paymentContract)
            return (innerContract, innerEntity);

        var delegateContract = new ArkDelegateContract(
            serverKey,
            exitDelay,
            paymentContract.User,
            delegateKey,
            cltvLocktime);

        // Preserve wallet ID and activity state from the inner entity
        var entity = delegateContract.ToEntity(
            innerEntity.WalletIdentifier, serverKey, null, innerEntity.ActivityState);

        return (delegateContract, entity);
    }
}
