using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NBitcoin.Secp256k1;

namespace NArk.Core.Transformers;

/// <summary>
/// Checks whether a contract can be delegated to a specific delegator
/// and provides the script builders needed for delegation artifacts.
/// Register multiple transformers to handle different contract types that support delegation.
/// </summary>
public interface IDelegationTransformer
{
    /// <summary>
    /// Returns true if this transformer recognises the contract as delegatable
    /// and the delegator's public key matches the expected delegate key in the contract.
    /// </summary>
    Task<bool> CanDelegate(string walletIdentifier, ArkContract contract, ECPubKey delegatePubkey);

    /// <summary>
    /// Returns the script builders for delegation artifacts:
    /// - intentScript: collaborative path for the BIP322 intent proof (e.g., User+Server 2-of-2)
    /// - forfeitScript: delegate path for ACP forfeit tx (e.g., User+Delegate+Server 3-of-3)
    /// </summary>
    (ScriptBuilder intentScript, ScriptBuilder forfeitScript) GetDelegationScriptBuilders(ArkContract contract);
}
