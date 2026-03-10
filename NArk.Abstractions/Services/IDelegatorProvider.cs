namespace NArk.Abstractions.Services;

/// <summary>
/// Provides communication with a delegator service (e.g. Fulmine)
/// for automatic VTXO rollover. The delegator monitors watched addresses
/// and participates in batch rounds on the owner's behalf before VTXOs expire.
/// </summary>
public interface IDelegatorProvider
{
    /// <summary>
    /// Returns the delegator's compressed public key (hex-encoded).
    /// Used when constructing delegate contract scripts.
    /// </summary>
    Task<string> GetDelegatePublicKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers an Ark address for automatic rollover by the delegator.
    /// </summary>
    /// <param name="address">The Ark address to watch.</param>
    /// <param name="tapscripts">Hex-encoded tap leaf scripts for the address's taproot tree.</param>
    /// <param name="destinationAddress">Where rolled-over funds should be sent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WatchAddressForRolloverAsync(
        string address,
        string[] tapscripts,
        string destinationAddress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops watching an Ark address for rollover.
    /// </summary>
    Task UnwatchAddressAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all addresses currently watched by the delegator.
    /// </summary>
    Task<IReadOnlyList<WatchedRolloverAddress>> ListWatchedAddressesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// An Ark address being watched by a delegator for automatic rollover.
/// </summary>
/// <param name="Address">The Ark address.</param>
/// <param name="Tapscripts">Hex-encoded tap leaf scripts.</param>
/// <param name="DestinationAddress">Where rolled-over funds are sent.</param>
public record WatchedRolloverAddress(string Address, string[] Tapscripts, string DestinationAddress);
