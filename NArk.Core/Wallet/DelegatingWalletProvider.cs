using NArk.Abstractions.Extensions;
using NArk.Abstractions.Services;
using NArk.Abstractions.Wallets;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Wallet;

/// <summary>
/// Decorator on <see cref="IWalletProvider"/> that wraps address providers
/// to produce <see cref="NArk.Core.Contracts.ArkDelegateContract"/> for HD wallets.
/// The delegator pubkey and server info are fetched lazily and cached.
/// </summary>
public class DelegatingWalletProvider(
    IWalletProvider inner,
    IDelegatorProvider delegatorProvider,
    IClientTransport clientTransport) : IWalletProvider
{
    private OutputDescriptor? _delegateKey;
    private ArkServerInfo? _serverInfo;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public Task<IArkadeWalletSigner?> GetSignerAsync(string identifier, CancellationToken cancellationToken = default)
        => inner.GetSignerAsync(identifier, cancellationToken);

    public async Task<IArkadeAddressProvider?> GetAddressProviderAsync(string identifier, CancellationToken cancellationToken = default)
    {
        var innerProvider = await inner.GetAddressProviderAsync(identifier, cancellationToken);
        if (innerProvider is null)
            return null;

        await EnsureInitializedAsync(cancellationToken);
        return new DelegatingAddressProvider(innerProvider, _delegateKey!, _serverInfo!.SignerKey, _serverInfo.UnilateralExit);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_delegateKey is not null)
            return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_delegateKey is not null)
                return;

            _serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
            var info = await delegatorProvider.GetDelegatorInfoAsync(cancellationToken);
            _delegateKey = KeyExtensions.ParseOutputDescriptor(info.Pubkey, _serverInfo.Network);
        }
        finally
        {
            _initLock.Release();
        }
    }
}
