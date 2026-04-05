using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;

namespace NArk.Wallet.Client.Services;

public class WalletState : IDisposable
{
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IContractStorage _contractStorage;

    public string? ActiveWalletId { get; private set; }
    public long BalanceSats { get; private set; }

    /// <summary>Asset IDs the user is tracking (imported or issued).</summary>
    public HashSet<string> TrackedAssetIds { get; } = new();

    public event Action? OnChange;

    public WalletState(IVtxoStorage vtxoStorage, IContractStorage contractStorage)
    {
        _vtxoStorage = vtxoStorage;
        _contractStorage = contractStorage;

        _vtxoStorage.VtxosChanged += OnVtxosChanged;
        _contractStorage.ContractsChanged += OnContractsChanged;
    }

    public void SetActiveWallet(string? walletId)
    {
        ActiveWalletId = walletId;
        OnChange?.Invoke();
    }

    public void UpdateBalance(long sats)
    {
        BalanceSats = sats;
        OnChange?.Invoke();
    }

    public void NotifyChanged() => OnChange?.Invoke();

    private void OnVtxosChanged(object? sender, ArkVtxo vtxo) => OnChange?.Invoke();
    private void OnContractsChanged(object? sender, ArkContractEntity contract) => OnChange?.Invoke();

    public void Dispose()
    {
        _vtxoStorage.VtxosChanged -= OnVtxosChanged;
        _contractStorage.ContractsChanged -= OnContractsChanged;
    }
}
