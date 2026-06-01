using NArk.Core.Services;
using NArk.Swaps.Services;

namespace NArk.Wallet.Client.Services;

/// <summary>
/// Extension to manually start SDK background services in Blazor WASM
/// (which doesn't support IHostedService).
/// </summary>
public static class ArkServiceStartup
{
    public static async Task StartArkServicesAsync(this IServiceProvider services)
    {
        var cts = new CancellationTokenSource();

        // Start services in the same order as ArkHostedLifecycle
        var sweeper = services.GetRequiredService<SweeperService>();
        await sweeper.StartAsync(cts.Token);

        var batch = services.GetRequiredService<BatchManagementService>();
        await batch.StartAsync(cts.Token);

        var intentSync = services.GetRequiredService<IntentSynchronizationService>();
        await intentSync.StartAsync(cts.Token);

        var intentGen = services.GetRequiredService<IntentGenerationService>();
        await intentGen.StartAsync(cts.Token);

        // Non-fatal if server subscription endpoint is unavailable (e.g. 500/501) —
        // VtxoSynchronizationService will fall back to routine polling.
        try
        {
            var vtxoSync = services.GetRequiredService<VtxoSynchronizationService>();
            await vtxoSync.StartAsync(cts.Token);
        }
        catch { /* Server subscription unavailable — VTXO sync will poll */ }

        // Start swap management (monitors swap status, handles claims).
        // Non-fatal if Boltz is unreachable — swaps just won't be monitored until next app load.
        try
        {
            var swapMgr = services.GetRequiredService<SwapsManagementService>();
            await swapMgr.StartAsync(cts.Token);
        }
        catch { /* Boltz unavailable — swap monitoring disabled */ }
    }
}
