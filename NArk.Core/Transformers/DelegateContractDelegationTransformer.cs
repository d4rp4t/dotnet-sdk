using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NBitcoin.Secp256k1;

namespace NArk.Core.Transformers;

public class DelegateContractDelegationTransformer(
    IWalletProvider walletProvider,
    ILogger<DelegateContractDelegationTransformer>? logger = null) : IDelegationTransformer
{
    public async Task<bool> CanDelegate(string walletIdentifier, ArkContract contract, string delegatePubkeyHex)
    {
        if (contract is not ArkDelegateContract delegateContract)
            return false;

        // Verify the delegator's pubkey matches the contract's delegate key
        var delegatorPubkey = ECXOnlyPubKey.Create(Convert.FromHexString(delegatePubkeyHex));
        if (!delegateContract.Delegate.ToXOnlyPubKey().Equals(delegatorPubkey))
        {
            logger?.LogDebug(
                "Delegator pubkey mismatch: contract={ContractDelegate}, delegator={DelegatorPubkey}",
                delegateContract.Delegate, delegatePubkeyHex);
            return false;
        }

        if (await walletProvider.GetAddressProviderAsync(walletIdentifier) is not { } addressProvider)
            return false;

        return await addressProvider.IsOurs(delegateContract.User);
    }
}
