using NArk.Abstractions.Contracts;

namespace NArk.Core.Transformers;

/// <summary>
/// Checks whether a contract can be delegated to a specific delegator.
/// Register multiple transformers to handle different contract types that support delegation.
/// </summary>
public interface IDelegationTransformer
{
    /// <summary>
    /// Returns true if this transformer recognises the contract as delegatable
    /// and the delegator's public key matches the expected delegate key in the contract.
    /// </summary>
    Task<bool> CanDelegate(string walletIdentifier, ArkContract contract, string delegatePubkeyHex);
}
