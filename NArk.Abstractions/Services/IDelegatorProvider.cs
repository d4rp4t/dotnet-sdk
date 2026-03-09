namespace NArk.Abstractions.Services;

/// <summary>
/// Provides communication with a delegator service (e.g. Fulmine)
/// for delegating VTXO renewal.
/// </summary>
public interface IDelegatorProvider
{
    /// <summary>
    /// Retrieves the delegator's public key, fee, and payment address.
    /// </summary>
    Task<DelegateInfo> GetDelegateInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a delegation request: a pre-signed intent and partial forfeit transactions.
    /// </summary>
    Task DelegateAsync(
        string intentMessage,
        string intentProof,
        string[] forfeitTxs,
        bool rejectReplace = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Information about a delegator service.
/// </summary>
/// <param name="Pubkey">Hex-encoded compressed public key of the delegator.</param>
/// <param name="Fee">Per-input fee in sats charged by the delegator.</param>
/// <param name="DelegatorAddress">Ark address where the delegator fee should be sent.</param>
public record DelegateInfo(string Pubkey, ulong Fee, string DelegatorAddress);
