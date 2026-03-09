using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;

namespace NArk.Core.Transformers;

/// <summary>
/// Transforms a contract + VTXO pair into a delegation-ready <see cref="ArkCoin"/>
/// that uses the delegate spending path.
/// Register multiple transformers to handle different contract types that support delegation.
/// </summary>
public interface IDelegationTransformer
{
    /// <summary>
    /// Returns true if this transformer can handle delegation for the given contract/VTXO pair
    /// with the specified delegator.
    /// </summary>
    Task<bool> CanDelegate(string walletIdentifier, ArkContract contract, ArkVtxo vtxo, DelegateInfo delegator);

    /// <summary>
    /// Transforms the contract/VTXO pair into a coin using the delegate spending path,
    /// suitable for building partial forfeit transactions.
    /// </summary>
    Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo, DelegateInfo delegator);
}
