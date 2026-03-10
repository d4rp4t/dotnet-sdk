# Automated VTXO Delegation

## Problem

VTXOs expire if not refreshed. A delegator service (e.g. Fulmine) can participate in batch rounds on the user's behalf, rolling VTXOs over before expiry. PR #33 added the delegation primitives (contract, gRPC client, proto), but nothing automates the flow: contract derivation still produces payment contracts, and receiving VTXOs doesn't trigger delegation.

## Requirements

1. When delegation is configured (`AddArkDelegation(uri)`), HD wallets derive `ArkDelegateContract` instead of `ArkPaymentContract` for Receive/SendToSelf purposes.
2. nsec wallets (hashlock/note contracts) are unaffected.
3. When VTXOs arrive at delegate contract addresses, the SDK automatically builds a partially-signed intent + ACP forfeit txs and sends them to the delegator.
4. The system is modular: other delegatable contract types can be added without modifying the monitor service.

## Architecture

Four components, all wired via `AddArkDelegation(uri)`:

```
                    VtxoStorage.VtxosChanged
                            |
                            v
                  DelegationMonitorService
                   /         |          \
    IDelegationTransformer   |    IDelegatorProvider
    (per contract type)      |    (gRPC to Fulmine)
                             |
                    Build intent + ACP forfeit txs
                             |
                             v
                    DelegateAsync(message, proof, forfeitTxs)
```

### Component 1: Contract Derivation Override

**Mechanism**: Decorator on `IWalletProvider` that wraps address providers returned by `GetAddressProviderAsync()`.

> **Why IWalletProvider, not IArkadeAddressProvider**: Address providers are created per-wallet by `IWalletProvider.GetAddressProviderAsync()` — they are not DI-registered singletons. The decorator wraps `IWalletProvider` and intercepts the returned `IArkadeAddressProvider` instances.

When delegation is configured:
- `GetNextContract(Receive)` and `GetNextContract(SendToSelf)` produce `ArkDelegateContract` with the cached delegator pubkey
- nsec wallets pass through (they use hashlock/note contracts, not payment contracts)
- Boarding contracts pass through unchanged

The decorator is registered by `AddArkDelegation()` and fetches the delegator pubkey once on first use (cached).

### Component 2: DelegateContractTransformer

`IContractTransformer` implementation for `ArkDelegateContract`:
- `CanTransform()`: checks contract type is `ArkDelegateContract` and User descriptor belongs to this wallet
- `Transform()`: returns `ArkCoin` with the **collaborative path** (`ForfeitPath()` = User + Server 2-of-2) as the spending script

This makes delegate VTXOs visible to `SpendingService` and `IntentGenerationService` for normal user-initiated operations.

### Component 3: IDelegationTransformer (expanded)

```csharp
public interface IDelegationTransformer
{
    Task<bool> CanDelegate(string walletIdentifier, ArkContract contract, ECPubKey delegatePubkey);

    (ScriptBuilder intentScript, ScriptBuilder forfeitScript) GetDelegationScriptBuilders(ArkContract contract);
}
```

- `CanDelegate`: checks contract type, verifies delegate pubkey matches, confirms User descriptor is ours
- `GetDelegationScriptBuilders`: returns two script builders per contract type:
  - `intentScript`: collaborative spend path for the BIP322 intent proof (User + Server)
  - `forfeitScript`: delegate path for the ACP forfeit tx (User + Delegate + Server)

**`DelegateContractDelegationTransformer`** implements this for `ArkDelegateContract`:
- `intentScript` = `ForfeitPath()` (User + Server 2-of-2)
- `forfeitScript` = `DelegatePath()` (User + Delegate + Server 3-of-3)

Other contract types can register their own `IDelegationTransformer` implementations.

### Component 4: DelegationMonitorService

Hosted service that auto-delegates when VTXOs arrive:

1. **Subscribe** to `IVtxoStorage.VtxosChanged`
2. **On new unspent VTXOs**: look up contract from `IContractStorage`, parse it
3. **Check eligibility**: iterate `IDelegationTransformer` instances, call `CanDelegate(wallet, contract, delegatePubkey)`
4. **Skip**: already-delegated outpoints (in-memory `HashSet<OutPoint>`)
5. **Get script builders**: `GetDelegationScriptBuilders(contract)` returns `(intentScript, forfeitScript)`
6. **Build delegation artifacts** (grouped by script/contract):
   - **RegisterMessage**: JSON with VTXO outpoints, cosigner keys (server + user + delegate), validity window
   - **Proof PSBT**: BIP322-style proof using `intentScript`, signed by user
   - **Forfeit txs**: built on `forfeitScript` path, signed with `SIGHASH_ALL | ANYONECANPAY` (user's partial signature; delegator adds connector input later during batch)
7. **Send**: `delegatorProvider.DelegateAsync(message, proof, forfeitTxs)`
8. **Track**: add outpoints to delegated set

### Dependencies (DI)

```csharp
DelegationMonitorService(
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    IEnumerable<IDelegationTransformer> transformers,
    IDelegatorProvider delegatorProvider,
    IWalletProvider walletProvider,
    IClientTransport clientTransport,
    ILogger<DelegationMonitorService>? logger)
```

### DI Registration

`AddArkDelegation(uri)` registers:
- `GrpcDelegatorProvider` as `IDelegatorProvider` (singleton, connects to delegator gRPC)
- `DelegateContractTransformer` as `IContractTransformer`
- `DelegationMonitorService` as hosted service
- `IWalletProvider` decorator for contract derivation override

> **Note**: `DelegateContractDelegationTransformer` as `IDelegationTransformer` is already registered in `AddArkCoreServices()` (line ~91). `AddArkDelegation()` must NOT re-register it.

`AddArkCoreServices()` continues to register `DelegationService` (the thin orchestrator for manual delegation calls) and `DelegateContractDelegationTransformer`.

## Forfeit TX Signing Details

The ACP forfeit tx uses `SIGHASH_ALL | ANYONECANPAY`:
- **ALL**: commits to all outputs (the VTXO spend destination is locked)
- **ANYONECANPAY**: only commits to the single VTXO input; the delegator adds the connector input from the batch tree later

The user signs through the delegate path tapscript leaf (User + Delegate + Server 3-of-3). The delegator completes signatures (delegate + server keys) when joining the batch.

## Prerequisites / Breaking Changes

1. **IDelegationTransformer interface change**: `CanDelegate` changes from `string delegatePubkeyHex` to `ECPubKey delegatePubkey`, and `GetDelegationScriptBuilders` is added. Existing `DelegateContractDelegationTransformer` must be updated.
2. **BIP322 proof extraction**: The proof-building logic is currently private to `IntentGenerationService.CreatePsbt`/`CreateIntent`. The monitor service will need similar logic — either extract to a shared helper or duplicate.
3. **ACP sighash support**: `PsbtHelpers.SignAndFillPsbt` may need modification to support `SIGHASH_ALL | ANYONECANPAY` for forfeit tx signing.
4. **Race condition with IntentGenerationService**: When delegation is configured, `IntentGenerationService` must skip VTXOs at delegate contracts to avoid conflicts with the event-driven `DelegationMonitorService`.

## What This Design Does NOT Cover

- Full intent creation reuse from `IntentGenerationService` internals (the monitor may need to duplicate some BIP322 proof logic or extract it into a shared helper)
- Delegator fee payment mechanics (the `DelegatorInfo.Fee` and `DelegatorAddress` fields exist but fee handling is deferred)
- Re-delegation on batch failure (if the delegator's batch fails, the VTXOs need re-delegation)
- CLTV locktime policy (how to choose the optional safety window block height)
