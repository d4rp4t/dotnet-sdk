using NArk.Swaps.Boltz.Models.Bolt12;

namespace NArk.Swaps.Boltz.Client;

public partial class BoltzClient
{
    /// <summary>
    /// Fetches a BOLT 12 invoice from the given offer via
    /// <c>POST /v2/lightning/{currency}/bolt12/fetch</c>.
    /// </summary>
    /// <param name="currency">
    /// The Lightning currency symbol, e.g. <c>"BTC"</c>.
    /// </param>
    /// <param name="request">Offer string, amount in satoshis, and optional note.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="Bolt12FetchResponse"/> containing the BOLT 12 invoice
    /// (<c>lni1…</c>) and an optional magic routing hint.
    /// </returns>
    /// <exception cref="HttpRequestException">
    /// Thrown when the currency has no BOLT 12 support (HTTP 404) or when
    /// Boltz could not reach the offer's node to obtain an invoice (HTTP 500).
    /// </exception>
    public virtual Task<Bolt12FetchResponse> FetchBolt12InvoiceAsync(
        string currency,
        Bolt12FetchRequest request,
        CancellationToken ct = default)
        => PostAsJsonAsync<Bolt12FetchRequest, Bolt12FetchResponse>(
            $"v2/lightning/{currency}/bolt12/fetch", request, ct);
}
