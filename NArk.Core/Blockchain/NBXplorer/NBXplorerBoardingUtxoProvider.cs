using NArk.Abstractions.Services;
using NBitcoin;
using NBXplorer;
using NBXplorer.Models;

namespace NArk.Blockchain.NBXplorer;

/// <summary>
/// Queries boarding UTXOs from an NBXplorer instance.
/// Automatically tracks each boarding address on first query (idempotent).
/// </summary>
public class NBXplorerBoardingUtxoProvider : IBoardingUtxoProvider
{
    private readonly ExplorerClient _explorerClient;

    public NBXplorerBoardingUtxoProvider(ExplorerClient explorerClient)
    {
        _explorerClient = explorerClient;
    }

    public NBXplorerBoardingUtxoProvider(Network network, Uri nbxplorerUri)
        : this(new ExplorerClient(new NBXplorerNetworkProvider(network.ChainName).GetBTC(), nbxplorerUri))
    {
    }

    public async Task<IReadOnlyList<BoardingUtxo>> GetUtxosAsync(string address, CancellationToken cancellationToken = default)
    {
        var bitcoinAddress = BitcoinAddress.Create(address, _explorerClient.Network.NBitcoinNetwork);
        var trackedSource = TrackedSource.Create(bitcoinAddress);

        // Ensure the address is tracked (idempotent)
        await _explorerClient.TrackAsync(trackedSource, cancellation: cancellationToken);

        var utxoChanges = await _explorerClient.GetUTXOsAsync(trackedSource, cancellationToken);

        // GetUnspentUTXOs applies the spent filter across confirmed + unconfirmed deltas.
        // Raw Confirmed.UTXOs / Unconfirmed.UTXOs are deltas that may include already-spent outputs.
        var unspent = utxoChanges.GetUnspentUTXOs().ToList();

        var results = new List<BoardingUtxo>(unspent.Count);
        foreach (var utxo in unspent)
        {
            var confirmed = utxo.Confirmations > 0;
            var blockHeight = confirmed
                ? utxoChanges.CurrentHeight - (int)utxo.Confirmations + 1
                : 0;

            results.Add(new BoardingUtxo(
                Txid: utxo.Outpoint.Hash.ToString(),
                Vout: (uint)utxo.Outpoint.N,
                Amount: ((Money)utxo.Value).Satoshi > 0 ? (ulong)((Money)utxo.Value).Satoshi : 0,
                Confirmed: confirmed,
                BlockHeight: blockHeight,
                BlockTime: confirmed ? utxo.Timestamp.ToUnixTimeSeconds() : 0));
        }

        return results;
    }
}
