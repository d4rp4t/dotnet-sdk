using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NArk.Wallet.Client.Services;

/// <summary>
/// Client-side LNURL-pay decoder and callback handler.
/// Handles lnurl1... bech32 strings and Lightning Addresses (user@domain).
/// </summary>
public class LnurlHelper(HttpClient http)
{
    public record LnurlPayParams(
        string Callback,
        long MinSendable,
        long MaxSendable,
        string? Description);

    /// <summary>
    /// Detects if the input is an LNURL or Lightning Address.
    /// </summary>
    public static bool IsLnurl(string input)
    {
        input = input.Trim();
        if (input.StartsWith("lnurl1", StringComparison.OrdinalIgnoreCase))
            return true;
        if (input.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase))
            return true;
        // Lightning Address: user@domain
        if (input.Contains('@') && !input.Contains(' ') && input.IndexOf('@') > 0)
            return true;
        return false;
    }

    /// <summary>
    /// Resolves an LNURL or Lightning Address to its pay parameters.
    /// </summary>
    public async Task<LnurlPayParams> ResolveAsync(string input)
    {
        input = input.Trim();

        string url;
        if (input.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase))
            input = input["lightning:".Length..];

        if (input.Contains('@'))
        {
            // Lightning Address → well-known URL
            var parts = input.Split('@', 2);
            url = $"https://{parts[1]}/.well-known/lnurlp/{parts[0]}";
        }
        else if (input.StartsWith("lnurl1", StringComparison.OrdinalIgnoreCase))
        {
            url = DecodeLnurl(input);
        }
        else
        {
            throw new ArgumentException("Not a valid LNURL or Lightning Address");
        }

        var response = await http.GetFromJsonAsync<LnurlPayResponse>(url)
            ?? throw new InvalidOperationException("Failed to fetch LNURL-pay params");

        if (response.Tag?.ToLower() != "payrequest")
            throw new InvalidOperationException($"Expected payRequest, got: {response.Tag}");

        return new LnurlPayParams(
            response.Callback,
            response.MinSendable / 1000, // Convert millisats to sats
            response.MaxSendable / 1000,
            response.Metadata);
    }

    /// <summary>
    /// Fetches a Lightning invoice from the LNURL-pay callback.
    /// </summary>
    public async Task<string> FetchInvoiceAsync(string callback, long amountSats)
    {
        var amountMsat = amountSats * 1000;
        var separator = callback.Contains('?') ? "&" : "?";
        var url = $"{callback}{separator}amount={amountMsat}";

        var response = await http.GetFromJsonAsync<LnurlCallbackResponse>(url)
            ?? throw new InvalidOperationException("Failed to fetch invoice from LNURL callback");

        if (!string.IsNullOrEmpty(response.Reason))
            throw new InvalidOperationException($"LNURL error: {response.Reason}");

        return response.Pr ?? throw new InvalidOperationException("No invoice in LNURL response");
    }

    private static string DecodeLnurl(string lnurl)
    {
        // Decode bech32 LNURL to URL
        var encoder = NBitcoin.DataEncoders.Encoders.Bech32("lnurl");
        encoder.StrictLength = false;
        encoder.SquashBytes = true;
        var data = encoder.DecodeDataRaw(lnurl.ToLower(), out _);
        return System.Text.Encoding.UTF8.GetString(data);
    }

    private record LnurlPayResponse
    {
        [JsonPropertyName("tag")]
        public string? Tag { get; init; }
        [JsonPropertyName("callback")]
        public string Callback { get; init; } = "";
        [JsonPropertyName("minSendable")]
        public long MinSendable { get; init; }
        [JsonPropertyName("maxSendable")]
        public long MaxSendable { get; init; }
        [JsonPropertyName("metadata")]
        public string? Metadata { get; init; }
    }

    private record LnurlCallbackResponse
    {
        [JsonPropertyName("pr")]
        public string? Pr { get; init; }
        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }
}
