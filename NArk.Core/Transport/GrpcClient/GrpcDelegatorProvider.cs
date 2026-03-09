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

    public async Task<DelegateInfo> GetDelegateInfoAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.GetDelegatorInfoAsync(
            new GetDelegatorInfoRequest(),
            cancellationToken: cancellationToken);

        var fee = ulong.TryParse(response.Fee, out var f) ? f : 0;
        return new DelegateInfo(response.Pubkey, fee, response.DelegatorAddress);
    }

    public async Task DelegateAsync(
        string intentMessage,
        string intentProof,
        string[] forfeitTxs,
        bool rejectReplace = false,
        CancellationToken cancellationToken = default)
    {
        var request = new DelegateRequest
        {
            Intent = new DelegateIntent
            {
                Message = intentMessage,
                Proof = intentProof
            },
            RejectReplace = rejectReplace
        };
        request.ForfeitTxs.AddRange(forfeitTxs);

        await _client.DelegateAsync(request, cancellationToken: cancellationToken);
    }
}
