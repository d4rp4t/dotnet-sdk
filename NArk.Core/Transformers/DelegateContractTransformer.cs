using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;

namespace NArk.Core.Transformers;

public class DelegateContractTransformer(IWalletProvider walletProvider, ILogger<DelegateContractTransformer>? logger = null) : IContractTransformer
{
    public async Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        if (contract is not ArkDelegateContract delegateContract)
            return false;

        if (await walletProvider.GetAddressProviderAsync(walletIdentifier) is not { } addressProvider)
            return false;

        if (!await addressProvider.IsOurs(delegateContract.User))
        {
            logger?.LogWarning(
                "DelegateContract user descriptor not ours: wallet={WalletId}, userDescriptor={UserDescriptor}",
                walletIdentifier, delegateContract.User);
            return false;
        }

        if (await walletProvider.GetSignerAsync(walletIdentifier) is null)
            return false;

        return true;
    }

    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var delegateContract = (contract as ArkDelegateContract)!;
        return new ArkCoin(walletIdentifier, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut,
            delegateContract.User ?? throw new InvalidOperationException("User is required for delegate contract"),
            delegateContract.CollaborativePath(), null, null, null, vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);
    }
}
