using Fulmine.V1;
using Grpc.Net.Client;
using NArk.Abstractions.Services;

namespace NArk.Transport.GrpcClient;

public class GrpcDelegatorProvider : IDelegatorProvider
{
    private readonly DelegatorService.DelegatorServiceClient _client;

    public GrpcDelegatorProvider(string uri)
    {
        var channel = GrpcChannel.ForAddress(uri);
        _client = new DelegatorService.DelegatorServiceClient(channel);
    }

    public async Task<string> GetDelegatePublicKeyAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.GetDelegatePublicKeyAsync(
            new GetDelegatePublicKeyRequest(),
            cancellationToken: cancellationToken);
        return response.PublicKey;
    }

    public async Task WatchAddressForRolloverAsync(
        string address,
        string[] tapscripts,
        string destinationAddress,
        CancellationToken cancellationToken = default)
    {
        var request = new WatchAddressForRolloverRequest
        {
            RolloverAddress = new RolloverAddress
            {
                Address = address,
                TaprootTree = new Tapscripts(),
                DestinationAddress = destinationAddress
            }
        };
        request.RolloverAddress.TaprootTree.Scripts.AddRange(tapscripts);

        await _client.WatchAddressForRolloverAsync(request, cancellationToken: cancellationToken);
    }

    public async Task UnwatchAddressAsync(string address, CancellationToken cancellationToken = default)
    {
        await _client.UnwatchAddressAsync(
            new UnwatchAddressRequest { Address = address },
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<WatchedRolloverAddress>> ListWatchedAddressesAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _client.ListWatchedAddressesAsync(
            new ListWatchedAddressesRequest(),
            cancellationToken: cancellationToken);

        return response.Addresses.Select(a => new WatchedRolloverAddress(
            a.Address,
            a.TaprootTree.Scripts.ToArray(),
            a.DestinationAddress)).ToList();
    }
}
