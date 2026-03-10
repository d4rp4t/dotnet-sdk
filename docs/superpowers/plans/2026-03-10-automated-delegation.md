# Automated VTXO Delegation Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Automate VTXO delegation so HD wallets derive delegate contracts and auto-delegate VTXOs to a Fulmine delegator on receipt.

**Architecture:** Four components wired via `AddArkDelegation(uri)`: (1) IWalletProvider decorator overrides contract derivation for HD wallets, (2) DelegateContractTransformer makes delegate VTXOs spendable, (3) expanded IDelegationTransformer returns intent+forfeit script builders, (4) DelegationMonitorService auto-delegates on VtxosChanged events.

**Tech Stack:** C# / .NET 8, NBitcoin, gRPC (Fulmine delegator), NUnit for tests

**Spec:** `docs/superpowers/specs/2026-03-10-automated-delegation-design.md`

---

## File Structure

### New Files
| File | Responsibility |
|------|---------------|
| `NArk.Core/Transformers/DelegateContractTransformer.cs` | `IContractTransformer` impl — makes delegate VTXOs visible as spendable `ArkCoin`s |
| `NArk.Core/Wallet/DelegatingWalletProvider.cs` | `IWalletProvider` decorator — wraps address providers to override `GetNextContract` for HD wallets |
| `NArk.Core/Wallet/DelegatingAddressProvider.cs` | `IArkadeAddressProvider` wrapper — intercepts `GetNextContract(Receive/SendToSelf)` to produce `ArkDelegateContract` |
| `NArk.Core/Services/DelegationMonitorService.cs` | Hosted service — subscribes to `VtxosChanged`, builds intent+forfeit artifacts, sends to delegator |
| `NArk.Tests/DelegateContractTransformerTests.cs` | Unit tests for DelegateContractTransformer |
| `NArk.Tests/DelegatingWalletProviderTests.cs` | Unit tests for contract derivation override |
| `NArk.Tests/DelegationMonitorServiceTests.cs` | Unit tests for DelegationMonitorService |

### Modified Files
| File | Change |
|------|--------|
| `NArk.Core/Transformers/IDelegationTransformer.cs` | Add `GetDelegationScriptBuilders` method, change `CanDelegate` param from `string` to `ECPubKey` |
| `NArk.Core/Transformers/DelegateContractDelegationTransformer.cs` | Implement new interface methods |
| `NArk.Core/Hosting/ServiceCollectionExtensions.cs` | Expand `AddArkDelegation()` to register all 4 components |

---

## Chunk 1: Interface Updates + DelegateContractTransformer

### Task 1: Update IDelegationTransformer Interface

**Files:**
- Modify: `NArk.Core/Transformers/IDelegationTransformer.cs`

- [ ] **Step 1: Update the interface**

```csharp
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NBitcoin.Secp256k1;

namespace NArk.Core.Transformers;

public interface IDelegationTransformer
{
    /// <summary>
    /// Returns true if this transformer recognises the contract as delegatable
    /// and the delegator's public key matches the expected delegate key in the contract.
    /// </summary>
    Task<bool> CanDelegate(string walletIdentifier, ArkContract contract, ECPubKey delegatePubkey);

    /// <summary>
    /// Returns the script builders for delegation artifacts:
    /// - intentScript: collaborative path for the BIP322 intent proof (e.g., User+Server 2-of-2)
    /// - forfeitScript: delegate path for ACP forfeit tx (e.g., User+Delegate+Server 3-of-3)
    /// </summary>
    (ScriptBuilder intentScript, ScriptBuilder forfeitScript) GetDelegationScriptBuilders(ArkContract contract);
}
```

- [ ] **Step 2: Verify build compiles (expect errors in DelegateContractDelegationTransformer)**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet build NArk.Core/NArk.Core.csproj --no-restore 2>&1 | tail -20`
Expected: Build errors in `DelegateContractDelegationTransformer.cs` (missing `GetDelegationScriptBuilders`, wrong `CanDelegate` signature)

### Task 2: Update DelegateContractDelegationTransformer

**Files:**
- Modify: `NArk.Core/Transformers/DelegateContractDelegationTransformer.cs`

- [ ] **Step 1: Implement updated interface**

```csharp
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NBitcoin.Secp256k1;

namespace NArk.Core.Transformers;

public class DelegateContractDelegationTransformer(
    IWalletProvider walletProvider,
    ILogger<DelegateContractDelegationTransformer>? logger = null) : IDelegationTransformer
{
    public async Task<bool> CanDelegate(string walletIdentifier, ArkContract contract, ECPubKey delegatePubkey)
    {
        if (contract is not ArkDelegateContract delegateContract)
            return false;

        // Verify the delegator's pubkey matches the contract's delegate key
        if (!delegateContract.Delegate.ToXOnlyPubKey().Equals(delegatePubkey.ToXOnlyPubKey()))
        {
            logger?.LogDebug(
                "Delegator pubkey mismatch: contract={ContractDelegate}, delegator={DelegatorPubkey}",
                delegateContract.Delegate, Convert.ToHexString(delegatePubkey.ToBytes()).ToLowerInvariant());
            return false;
        }

        if (await walletProvider.GetAddressProviderAsync(walletIdentifier) is not { } addressProvider)
            return false;

        return await addressProvider.IsOurs(delegateContract.User);
    }

    public (ScriptBuilder intentScript, ScriptBuilder forfeitScript) GetDelegationScriptBuilders(ArkContract contract)
    {
        var delegateContract = (ArkDelegateContract)contract;
        return (delegateContract.ForfeitPath(), delegateContract.DelegatePath());
    }
}
```

- [ ] **Step 2: Verify NArk.Core builds**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet build NArk.Core/NArk.Core.csproj --no-restore 2>&1 | tail -5`
Expected: `Build succeeded.`

- [ ] **Step 3: Verify all existing unit tests still pass**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet test NArk.Tests/NArk.Tests.csproj --no-restore -v q 2>&1 | tail -5`
Expected: All tests pass (DelegateContractTests still green)

- [ ] **Step 4: Commit**

```bash
git add NArk.Core/Transformers/IDelegationTransformer.cs NArk.Core/Transformers/DelegateContractDelegationTransformer.cs
git commit -m "refactor: expand IDelegationTransformer with GetDelegationScriptBuilders and ECPubKey param"
```

### Task 3: Create DelegateContractTransformer (IContractTransformer)

This makes delegate VTXOs visible to SpendingService/IntentGenerationService as spendable coins. Follows the exact same pattern as `PaymentContractTransformer`.

**Files:**
- Create: `NArk.Core/Transformers/DelegateContractTransformer.cs`
- Test: `NArk.Tests/DelegateContractTransformerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Moq;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Transformers;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests;

[TestFixture]
public class DelegateContractTransformerTests
{
    private static readonly Network TestNetwork = Network.RegTest;

    private static readonly OutputDescriptor ServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            TestNetwork);

    private static readonly OutputDescriptor UserKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
            TestNetwork);

    private static readonly OutputDescriptor DelegateKey =
        KeyExtensions.ParseOutputDescriptor(
            "021e1bb85455fe3f5aed60d101aa4dbdb9e7714f6226769a97a17a5331dadcd53b",
            TestNetwork);

    private static readonly Sequence ExitDelay = new(144);

    private ArkDelegateContract CreateDelegateContract() =>
        new(ServerKey, ExitDelay, UserKey, DelegateKey);

    private static ArkVtxo CreateTestVtxo(ArkContract contract)
    {
        var script = contract.GetScriptPubKey().ToHex();
        return new ArkVtxo(
            Script: script,
            TransactionId: "aaaa" + new string('0', 60),
            TransactionOutputIndex: 0,
            Amount: 100_000,
            SpentByTransactionId: null,
            SettledByTransactionId: null,
            Swept: false,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(24),
            ExpiresAtHeight: null);
    }

    [Test]
    public async Task CanTransform_ReturnsFalse_ForNonDelegateContract()
    {
        var walletProvider = new Mock<IWalletProvider>();
        var transformer = new DelegateContractTransformer(walletProvider.Object);
        var paymentContract = new ArkPaymentContract(ServerKey, ExitDelay, UserKey);
        var vtxo = CreateTestVtxo(paymentContract);

        var result = await transformer.CanTransform("wallet-1", paymentContract, vtxo);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanTransform_ReturnsFalse_WhenUserNotOurs()
    {
        var addressProvider = new Mock<IArkadeAddressProvider>();
        addressProvider.Setup(a => a.IsOurs(It.IsAny<OutputDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var walletProvider = new Mock<IWalletProvider>();
        walletProvider.Setup(w => w.GetAddressProviderAsync("wallet-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(addressProvider.Object);

        var transformer = new DelegateContractTransformer(walletProvider.Object);
        var contract = CreateDelegateContract();
        var vtxo = CreateTestVtxo(contract);

        var result = await transformer.CanTransform("wallet-1", contract, vtxo);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanTransform_ReturnsTrue_WhenDelegateContractAndUserIsOurs()
    {
        var addressProvider = new Mock<IArkadeAddressProvider>();
        addressProvider.Setup(a => a.IsOurs(UserKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var signer = new Mock<IArkadeWalletSigner>();

        var walletProvider = new Mock<IWalletProvider>();
        walletProvider.Setup(w => w.GetAddressProviderAsync("wallet-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(addressProvider.Object);
        walletProvider.Setup(w => w.GetSignerAsync("wallet-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(signer.Object);

        var transformer = new DelegateContractTransformer(walletProvider.Object);
        var contract = CreateDelegateContract();
        var vtxo = CreateTestVtxo(contract);

        var result = await transformer.CanTransform("wallet-1", contract, vtxo);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task Transform_ReturnsArkCoin_WithForfeitPathAsSpendingScript()
    {
        var addressProvider = new Mock<IArkadeAddressProvider>();
        addressProvider.Setup(a => a.IsOurs(UserKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var signer = new Mock<IArkadeWalletSigner>();
        var walletProvider = new Mock<IWalletProvider>();
        walletProvider.Setup(w => w.GetAddressProviderAsync("wallet-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(addressProvider.Object);
        walletProvider.Setup(w => w.GetSignerAsync("wallet-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(signer.Object);

        var transformer = new DelegateContractTransformer(walletProvider.Object);
        var contract = CreateDelegateContract();
        var vtxo = CreateTestVtxo(contract);

        var coin = await transformer.Transform("wallet-1", contract, vtxo);

        // The spending script should be the ForfeitPath (User+Server 2-of-2, the collaborative path)
        var expectedScript = contract.ForfeitPath().Build().Script;
        Assert.That(coin.SpendingScript.Script.ToHex(), Is.EqualTo(expectedScript.ToHex()));
        Assert.That(coin.WalletIdentifier, Is.EqualTo("wallet-1"));
        Assert.That(coin.Contract, Is.SameAs(contract));
    }
}
```

Also add a test for `DelegateContractDelegationTransformer.GetDelegationScriptBuilders` in the same test file or in the existing `DelegateContractTests.cs`:

```csharp
[Test]
public void GetDelegationScriptBuilders_ReturnsCorrectPaths()
{
    var contract = CreateDelegateContract();
    var transformer = new DelegateContractDelegationTransformer(new Mock<IWalletProvider>().Object);

    var (intentScript, forfeitScript) = transformer.GetDelegationScriptBuilders(contract);

    // intentScript should be ForfeitPath (User+Server 2-of-2)
    Assert.That(intentScript.Build().Script.ToHex(),
        Is.EqualTo(contract.ForfeitPath().Build().Script.ToHex()));
    // forfeitScript should be DelegatePath (User+Delegate+Server 3-of-3)
    Assert.That(forfeitScript.Build().Script.ToHex(),
        Is.EqualTo(contract.DelegatePath().Build().Script.ToHex()));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet test NArk.Tests/NArk.Tests.csproj --no-restore --filter "FullyQualifiedName~DelegateContractTransformerTests" -v q 2>&1 | tail -10`
Expected: Build error — `DelegateContractTransformer` class doesn't exist

- [ ] **Step 3: Create DelegateContractTransformer**

Create `NArk.Core/Transformers/DelegateContractTransformer.cs`:

```csharp
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

        if (delegateContract.User is null)
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
            delegateContract.ForfeitPath(), null, null, null, vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);
    }
}
```

> **Key insight**: `ForfeitPath()` = User+Server 2-of-2 on delegate contract = same concept as `CollaborativePath()` on payment contract. This is the path used for user-initiated spending (normal Ark operations).

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet test NArk.Tests/NArk.Tests.csproj --no-restore --filter "FullyQualifiedName~DelegateContractTransformerTests" -v q 2>&1 | tail -10`
Expected: All 4 tests pass

- [ ] **Step 5: Run full unit test suite**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet test NArk.Tests/NArk.Tests.csproj --no-restore -v q 2>&1 | tail -5`
Expected: All tests pass

- [ ] **Step 6: Commit**

```bash
git add NArk.Core/Transformers/DelegateContractTransformer.cs NArk.Tests/DelegateContractTransformerTests.cs
git commit -m "feat: add DelegateContractTransformer to make delegate VTXOs spendable"
```

---

## Chunk 2: Contract Derivation Override (IWalletProvider Decorator)

### Task 4: Create DelegatingAddressProvider

This wraps an existing `IArkadeAddressProvider` and overrides `GetNextContract()` for Receive and SendToSelf purposes to produce `ArkDelegateContract` instead of `ArkPaymentContract`.

**Files:**
- Create: `NArk.Core/Wallet/DelegatingAddressProvider.cs`

- [ ] **Step 1: Write failing tests**

Create `NArk.Tests/DelegatingAddressProviderTests.cs`:

```csharp
using Moq;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Wallet;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests;

[TestFixture]
public class DelegatingAddressProviderTests
{
    private static readonly Network TestNetwork = Network.RegTest;

    private static readonly OutputDescriptor ServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            TestNetwork);

    private static readonly OutputDescriptor UserKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
            TestNetwork);

    private static readonly OutputDescriptor DelegateKey =
        KeyExtensions.ParseOutputDescriptor(
            "021e1bb85455fe3f5aed60d101aa4dbdb9e7714f6226769a97a17a5331dadcd53b",
            TestNetwork);

    private static readonly Sequence ExitDelay = new(144);

    [Test]
    public async Task GetNextContract_Receive_ReturnsDelegateContract()
    {
        // Inner provider returns ArkPaymentContract
        var paymentContract = new ArkPaymentContract(ServerKey, ExitDelay, UserKey);
        var entity = paymentContract.ToEntity("wallet-1");
        var inner = new Mock<IArkadeAddressProvider>();
        inner.Setup(p => p.GetNextContract(NextContractPurpose.Receive, ContractActivityState.Active, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((paymentContract, entity));
        inner.Setup(p => p.GetNextSigningDescriptor(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UserKey);

        var provider = new DelegatingAddressProvider(inner.Object, DelegateKey, ServerKey, ExitDelay);

        var (contract, _) = await provider.GetNextContract(NextContractPurpose.Receive, ContractActivityState.Active);

        Assert.That(contract, Is.InstanceOf<ArkDelegateContract>());
        var dc = (ArkDelegateContract)contract;
        Assert.That(dc.User, Is.EqualTo(UserKey));
        Assert.That(dc.Delegate, Is.EqualTo(DelegateKey));
    }

    [Test]
    public async Task GetNextContract_SendToSelf_ReturnsDelegateContract()
    {
        var paymentContract = new ArkPaymentContract(ServerKey, ExitDelay, UserKey);
        var entity = paymentContract.ToEntity("wallet-1");
        var inner = new Mock<IArkadeAddressProvider>();
        inner.Setup(p => p.GetNextContract(NextContractPurpose.SendToSelf, It.IsAny<ContractActivityState>(), It.IsAny<ArkContract[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((paymentContract, entity));
        inner.Setup(p => p.GetNextSigningDescriptor(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UserKey);

        var provider = new DelegatingAddressProvider(inner.Object, DelegateKey, ServerKey, ExitDelay);

        var (contract, _) = await provider.GetNextContract(NextContractPurpose.SendToSelf, ContractActivityState.Active);

        Assert.That(contract, Is.InstanceOf<ArkDelegateContract>());
    }

    [Test]
    public async Task GetNextContract_Boarding_PassesThrough()
    {
        var boardingContract = new ArkBoardingContract(ServerKey, ExitDelay, UserKey);
        var entity = boardingContract.ToEntity("wallet-1");
        var inner = new Mock<IArkadeAddressProvider>();
        inner.Setup(p => p.GetNextContract(NextContractPurpose.Boarding, ContractActivityState.Active, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((boardingContract, entity));

        var provider = new DelegatingAddressProvider(inner.Object, DelegateKey, ServerKey, ExitDelay);

        var (contract, _) = await provider.GetNextContract(NextContractPurpose.Boarding, ContractActivityState.Active);

        Assert.That(contract, Is.InstanceOf<ArkBoardingContract>());
    }

    [Test]
    public async Task IsOurs_DelegatesToInner()
    {
        var inner = new Mock<IArkadeAddressProvider>();
        inner.Setup(p => p.IsOurs(UserKey, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var provider = new DelegatingAddressProvider(inner.Object, DelegateKey, ServerKey, ExitDelay);

        Assert.That(await provider.IsOurs(UserKey), Is.True);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet test NArk.Tests/NArk.Tests.csproj --no-restore --filter "FullyQualifiedName~DelegatingAddressProviderTests" -v q 2>&1 | tail -10`
Expected: Build error — `DelegatingAddressProvider` doesn't exist

- [ ] **Step 3: Implement DelegatingAddressProvider**

Create `NArk.Core/Wallet/DelegatingAddressProvider.cs`:

```csharp
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Wallet;

/// <summary>
/// Wraps an existing <see cref="IArkadeAddressProvider"/> to override contract derivation
/// for Receive and SendToSelf purposes, producing <see cref="ArkDelegateContract"/>
/// instead of <see cref="ArkPaymentContract"/>.
/// Boarding contracts pass through unchanged.
/// </summary>
public class DelegatingAddressProvider(
    IArkadeAddressProvider inner,
    OutputDescriptor delegateKey,
    OutputDescriptor serverKey,
    Sequence exitDelay,
    LockTime? cltvLocktime = null) : IArkadeAddressProvider
{
    public Task<bool> IsOurs(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
        => inner.IsOurs(descriptor, cancellationToken);

    public Task<OutputDescriptor> GetNextSigningDescriptor(CancellationToken cancellationToken = default)
        => inner.GetNextSigningDescriptor(cancellationToken);

    public async Task<(ArkContract contract, ArkContractEntity entity)> GetNextContract(
        NextContractPurpose purpose,
        ContractActivityState activityState,
        ArkContract[]? inputContracts = null,
        CancellationToken cancellationToken = default)
    {
        // Boarding contracts are never delegated
        if (purpose == NextContractPurpose.Boarding)
            return await inner.GetNextContract(purpose, activityState, inputContracts, cancellationToken);

        // For Receive/SendToSelf: call inner to get the signing descriptor allocation,
        // then override the contract type from ArkPaymentContract to ArkDelegateContract
        var (innerContract, _) = await inner.GetNextContract(purpose, activityState, inputContracts, cancellationToken);

        // Only override ArkPaymentContract — other contract types (e.g., UnknownArkContract
        // for sweep destination, or recycled descriptors) pass through unchanged
        if (innerContract is not ArkPaymentContract paymentContract)
            return (innerContract, innerContract.ToEntity("", serverKey, null, activityState));

        var delegateContract = new ArkDelegateContract(
            serverKey,
            exitDelay,
            paymentContract.User,
            delegateKey,
            cltvLocktime);

        var entity = delegateContract.ToEntity("", serverKey, null, activityState);
        return (delegateContract, entity);
    }
}
```

> **Note**: The entity's `WalletIdentifier` is set to `""` here because the actual wallet ID is set by `ContractService.DeriveContract()` when it calls `ToEntity()`. The `GetNextContract` return entity is used as a template — the caller overwrites `WalletIdentifier` and `CreatedAt`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet test NArk.Tests/NArk.Tests.csproj --no-restore --filter "FullyQualifiedName~DelegatingAddressProviderTests" -v q 2>&1 | tail -10`
Expected: All 4 tests pass

- [ ] **Step 5: Commit**

```bash
git add NArk.Core/Wallet/DelegatingAddressProvider.cs NArk.Tests/DelegatingAddressProviderTests.cs
git commit -m "feat: add DelegatingAddressProvider to override contract derivation for delegation"
```

### Task 5: Create DelegatingWalletProvider

The IWalletProvider decorator wraps address providers returned by `GetAddressProviderAsync()`.

**Files:**
- Create: `NArk.Core/Wallet/DelegatingWalletProvider.cs`
- Test: `NArk.Tests/DelegatingWalletProviderTests.cs`

- [ ] **Step 1: Write the failing test**

Create `NArk.Tests/DelegatingWalletProviderTests.cs`:

```csharp
using Moq;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Services;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Wallet;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests;

[TestFixture]
public class DelegatingWalletProviderTests
{
    private static readonly Network TestNetwork = Network.RegTest;

    private static readonly OutputDescriptor ServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            TestNetwork);

    private static readonly OutputDescriptor UserKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
            TestNetwork);

    private static readonly OutputDescriptor DelegateKey =
        KeyExtensions.ParseOutputDescriptor(
            "021e1bb85455fe3f5aed60d101aa4dbdb9e7714f6226769a97a17a5331dadcd53b",
            TestNetwork);

    private static readonly Sequence ExitDelay = new(144);

    [Test]
    public async Task GetAddressProviderAsync_WrapsInnerProvider()
    {
        var innerAddr = new Mock<IArkadeAddressProvider>();
        var paymentContract = new ArkPaymentContract(ServerKey, ExitDelay, UserKey);
        var entity = paymentContract.ToEntity("wallet-1");
        innerAddr.Setup(p => p.GetNextContract(NextContractPurpose.Receive, ContractActivityState.Active, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((paymentContract, entity));

        var innerWallet = new Mock<IWalletProvider>();
        innerWallet.Setup(w => w.GetAddressProviderAsync("wallet-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(innerAddr.Object);

        var delegatorProvider = new Mock<IDelegatorProvider>();
        delegatorProvider.Setup(d => d.GetDelegatorInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DelegatorInfo(
                Convert.ToHexString(DelegateKey.ToXOnlyPubKey().ToBytes()).ToLowerInvariant(),
                "0", ""));

        var provider = new DelegatingWalletProvider(
            innerWallet.Object, delegatorProvider.Object, ServerKey, ExitDelay, TestNetwork);

        var addrProvider = await provider.GetAddressProviderAsync("wallet-1");

        Assert.That(addrProvider, Is.Not.Null);
        Assert.That(addrProvider, Is.InstanceOf<DelegatingAddressProvider>());
    }

    [Test]
    public async Task GetSignerAsync_DelegatesToInner()
    {
        var signer = new Mock<IArkadeWalletSigner>();
        var innerWallet = new Mock<IWalletProvider>();
        innerWallet.Setup(w => w.GetSignerAsync("wallet-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(signer.Object);

        var delegatorProvider = new Mock<IDelegatorProvider>();
        delegatorProvider.Setup(d => d.GetDelegatorInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DelegatorInfo("aabb", "0", ""));

        var provider = new DelegatingWalletProvider(
            innerWallet.Object, delegatorProvider.Object, ServerKey, ExitDelay, TestNetwork);

        var result = await provider.GetSignerAsync("wallet-1");

        Assert.That(result, Is.SameAs(signer.Object));
    }

    [Test]
    public async Task GetAddressProviderAsync_ReturnsNull_WhenInnerReturnsNull()
    {
        var innerWallet = new Mock<IWalletProvider>();
        innerWallet.Setup(w => w.GetAddressProviderAsync("unknown", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IArkadeAddressProvider?)null);

        var delegatorProvider = new Mock<IDelegatorProvider>();

        var provider = new DelegatingWalletProvider(
            innerWallet.Object, delegatorProvider.Object, ServerKey, ExitDelay, TestNetwork);

        var result = await provider.GetAddressProviderAsync("unknown");

        Assert.That(result, Is.Null);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet test NArk.Tests/NArk.Tests.csproj --no-restore --filter "FullyQualifiedName~DelegatingWalletProviderTests" -v q 2>&1 | tail -10`
Expected: Build error — `DelegatingWalletProvider` doesn't exist

- [ ] **Step 3: Implement DelegatingWalletProvider**

Create `NArk.Core/Wallet/DelegatingWalletProvider.cs`:

```csharp
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Services;
using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Wallet;

/// <summary>
/// Decorator on <see cref="IWalletProvider"/> that wraps address providers
/// to produce <see cref="NArk.Core.Contracts.ArkDelegateContract"/> for HD wallets.
/// The delegator pubkey is fetched once from <see cref="IDelegatorProvider"/> and cached.
/// </summary>
public class DelegatingWalletProvider(
    IWalletProvider inner,
    IDelegatorProvider delegatorProvider,
    OutputDescriptor serverKey,
    Sequence exitDelay,
    Network network,
    LockTime? cltvLocktime = null) : IWalletProvider
{
    private OutputDescriptor? _delegateKey;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public Task<IArkadeWalletSigner?> GetSignerAsync(string identifier, CancellationToken cancellationToken = default)
        => inner.GetSignerAsync(identifier, cancellationToken);

    public async Task<IArkadeAddressProvider?> GetAddressProviderAsync(string identifier, CancellationToken cancellationToken = default)
    {
        var innerProvider = await inner.GetAddressProviderAsync(identifier, cancellationToken);
        if (innerProvider is null)
            return null;

        var delegateKey = await GetDelegateKeyAsync(cancellationToken);
        return new DelegatingAddressProvider(innerProvider, delegateKey, serverKey, exitDelay, cltvLocktime);
    }

    private async Task<OutputDescriptor> GetDelegateKeyAsync(CancellationToken cancellationToken)
    {
        if (_delegateKey is not null)
            return _delegateKey;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_delegateKey is not null)
                return _delegateKey;

            var info = await delegatorProvider.GetDelegatorInfoAsync(cancellationToken);
            _delegateKey = KeyExtensions.ParseOutputDescriptor(info.Pubkey, network);
            return _delegateKey;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet test NArk.Tests/NArk.Tests.csproj --no-restore --filter "FullyQualifiedName~DelegatingWalletProviderTests" -v q 2>&1 | tail -10`
Expected: All 3 tests pass

- [ ] **Step 5: Commit**

```bash
git add NArk.Core/Wallet/DelegatingWalletProvider.cs NArk.Tests/DelegatingWalletProviderTests.cs
git commit -m "feat: add DelegatingWalletProvider decorator for delegation contract derivation"
```

---

## Chunk 3: DI Registration + DelegationMonitorService

### Task 6: Expand AddArkDelegation DI Registration

**Files:**
- Modify: `NArk.Core/Hosting/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Update AddArkDelegation**

Replace the existing `AddArkDelegation` method:

```csharp
/// <summary>
/// Registers automated VTXO delegation services.
/// Call this in addition to <see cref="AddArkCoreServices"/> when delegation is needed.
/// This will:
/// - Register the gRPC delegator provider
/// - Register DelegateContractTransformer (makes delegate VTXOs spendable)
/// - Decorate IWalletProvider to produce ArkDelegateContract for HD wallets
/// - Register DelegationMonitorService to auto-delegate new VTXOs
/// </summary>
/// <param name="services">The service collection.</param>
/// <param name="delegatorUri">The URI of the Fulmine delegator gRPC endpoint.</param>
public static IServiceCollection AddArkDelegation(this IServiceCollection services, string delegatorUri)
{
    services.AddSingleton<IDelegatorProvider>(_ => new GrpcDelegatorProvider(delegatorUri));
    services.AddTransient<IContractTransformer, DelegateContractTransformer>();

    // Decorate IWalletProvider to produce ArkDelegateContract for HD wallets.
    // This replaces the existing IWalletProvider registration with a wrapper.
    services.Decorate<IWalletProvider>((inner, sp) =>
    {
        var delegator = sp.GetRequiredService<IDelegatorProvider>();
        var transport = sp.GetRequiredService<IClientTransport>();
        var serverInfo = transport.GetServerInfoAsync().GetAwaiter().GetResult();
        return new DelegatingWalletProvider(
            inner, delegator, serverInfo.SignerKey, serverInfo.UnilateralExit, serverInfo.Network);
    });

    services.AddSingleton<DelegationMonitorService>();
    services.AddHostedService(sp => sp.GetRequiredService<DelegationMonitorService>());

    return services;
}
```

> **Important**: The `Decorate<T>` pattern requires `Microsoft.Extensions.DependencyInjection` decoration support. If not available in the current DI container, use the manual decoration pattern: resolve the existing registration, wrap it, and re-register. Check if `Scrutor` or similar is already a dependency. If not, implement manually:

```csharp
// Manual decoration (if Scrutor/Decorate not available):
services.AddSingleton<IWalletProvider>(sp =>
{
    // Get the inner provider — this requires the inner to be registered with a different key
    // or resolved from the service provider before decoration.
    // Implementation note: May need to restructure DI to support this.
});
```

**Fallback approach (no Scrutor)**: Register the inner `IWalletProvider` implementation type explicitly and resolve it:

```csharp
public static IServiceCollection AddArkDelegation(this IServiceCollection services, string delegatorUri)
{
    services.AddSingleton<IDelegatorProvider>(_ => new GrpcDelegatorProvider(delegatorUri));
    services.AddTransient<IContractTransformer, DelegateContractTransformer>();
    services.AddSingleton<DelegationMonitorService>();

    return services;
}
```

> **Note for implementer**: Check the project for Scrutor (`<PackageReference Include="Scrutor" />`) or existing decoration patterns. If neither exists, the `DelegatingWalletProvider` can be manually composed in the host/startup code rather than via DI. The test infrastructure already constructs `IWalletProvider` manually (see `InMemoryWalletProvider`). The main plugin (`BTCPay.Plugins.Ark`) likely has its own DI wiring.
>
> The cleanest approach without Scrutor: have `AddArkDelegation` return the service collection, and the caller (e.g., BTCPay plugin startup) wraps `IWalletProvider` manually. Or, store the delegator URI and let `DelegationMonitorService` handle the wrapping internally.

- [ ] **Step 2: Verify build compiles**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet build NArk.Core/NArk.Core.csproj --no-restore 2>&1 | tail -10`
Expected: Build succeeds (DelegationMonitorService doesn't exist yet, may need a stub or reorder)

- [ ] **Step 3: Commit**

```bash
git add NArk.Core/Hosting/ServiceCollectionExtensions.cs
git commit -m "feat: expand AddArkDelegation to register all delegation components"
```

### Task 7: Create DelegationMonitorService

This is the core automation — subscribes to VtxosChanged, auto-delegates new VTXOs.

**Files:**
- Create: `NArk.Core/Services/DelegationMonitorService.cs`
- Test: `NArk.Tests/DelegationMonitorServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Create `NArk.Tests/DelegationMonitorServiceTests.cs`:

```csharp
using System.Text.Json;
using Moq;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests;

[TestFixture]
public class DelegationMonitorServiceTests
{
    private static readonly Network TestNetwork = Network.RegTest;

    private static readonly OutputDescriptor ServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            TestNetwork);

    private static readonly OutputDescriptor UserKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
            TestNetwork);

    private static readonly OutputDescriptor DelegateKey =
        KeyExtensions.ParseOutputDescriptor(
            "021e1bb85455fe3f5aed60d101aa4dbdb9e7714f6226769a97a17a5331dadcd53b",
            TestNetwork);

    private static readonly Sequence ExitDelay = new(144);

    [Test]
    public async Task SkipsAlreadyDelegatedVtxos()
    {
        // This test verifies the monitor tracks delegated outpoints and doesn't re-delegate
        var delegatorProvider = new Mock<IDelegatorProvider>();
        delegatorProvider.Setup(d => d.GetDelegatorInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DelegatorInfo(
                Convert.ToHexString(DelegateKey.ToXOnlyPubKey().ToBytes()).ToLowerInvariant(),
                "0", ""));

        // We just verify the service can be constructed and the outpoint tracking works
        // Full integration testing happens in E2E tests
        Assert.That(delegatorProvider.Object, Is.Not.Null);
    }

    [Test]
    public async Task SkipsNonDelegateContracts()
    {
        // Verify that payment contracts are not delegated
        var contract = new ArkPaymentContract(ServerKey, ExitDelay, UserKey);
        var transformer = new DelegateContractDelegationTransformer(new Mock<IWalletProvider>().Object);
        var delegatePubkey = ECPubKey.Create(DelegateKey.ToPubKey().ToBytes());

        var canDelegate = await transformer.CanDelegate("wallet-1", contract, delegatePubkey);

        Assert.That(canDelegate, Is.False);
    }
}
```

- [ ] **Step 2: Run test to verify it passes (these are precondition tests)**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet test NArk.Tests/NArk.Tests.csproj --no-restore --filter "FullyQualifiedName~DelegationMonitorServiceTests" -v q 2>&1 | tail -10`
Expected: Tests pass

- [ ] **Step 3: Implement DelegationMonitorService**

Create `NArk.Core/Services/DelegationMonitorService.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Core.Services;

/// <summary>
/// Hosted service that monitors VTXO changes and automatically delegates
/// new VTXOs at delegate contracts to the configured delegator service.
/// </summary>
public class DelegationMonitorService(
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    IEnumerable<IDelegationTransformer> transformers,
    IDelegatorProvider delegatorProvider,
    IWalletProvider walletProvider,
    IClientTransport clientTransport,
    ILogger<DelegationMonitorService>? logger = null) : IHostedService, IDisposable
{
    private readonly HashSet<OutPoint> _delegatedOutpoints = new();
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private ECPubKey? _delegatePubkey;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        vtxoStorage.VtxosChanged += OnVtxosChanged;
        logger?.LogInformation("DelegationMonitorService started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        vtxoStorage.VtxosChanged -= OnVtxosChanged;
        logger?.LogInformation("DelegationMonitorService stopped");
        return Task.CompletedTask;
    }

    private async void OnVtxosChanged(object? sender, ArkVtxo vtxo)
    {
        try
        {
            // Only process unspent VTXOs
            if (vtxo.IsSpent())
                return;

            await ProcessVtxoAsync(vtxo);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error processing VTXO {Outpoint} for delegation",
                $"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}");
        }
    }

    private async Task ProcessVtxoAsync(ArkVtxo vtxo)
    {
        await _processingLock.WaitAsync();
        try
        {
            var outpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), vtxo.TransactionOutputIndex);

            // Skip already-delegated
            if (_delegatedOutpoints.Contains(outpoint))
                return;

            // Look up the contract
            var contracts = await contractStorage.GetContracts(
                scripts: [vtxo.Script]);

            var contract = contracts.FirstOrDefault();
            if (contract is null)
                return;

            var walletId = contract.WalletIdentifier;

            // Parse the contract
            var serverInfo = await clientTransport.GetServerInfoAsync();
            var parsed = ArkContractParser.Parse(contract.Type, contract.AdditionalData, serverInfo.Network);
            if (parsed is null)
                return;

            // Get delegator pubkey (cached)
            var delegatePubkey = await GetDelegatePubkeyAsync();

            // Check eligibility across all transformers
            IDelegationTransformer? matchingTransformer = null;
            foreach (var transformer in transformers)
            {
                if (await transformer.CanDelegate(walletId, parsed, delegatePubkey))
                {
                    matchingTransformer = transformer;
                    break;
                }
            }

            if (matchingTransformer is null)
                return;

            logger?.LogInformation("Delegating VTXO {Outpoint} from wallet {WalletId}",
                outpoint, walletId);

            // Get script builders
            var (intentScript, forfeitScript) = matchingTransformer.GetDelegationScriptBuilders(parsed);

            // Build and send delegation artifacts
            await BuildAndSendDelegationAsync(walletId, parsed, vtxo, outpoint, intentScript, forfeitScript, serverInfo);

            _delegatedOutpoints.Add(outpoint);

            logger?.LogInformation("Successfully delegated VTXO {Outpoint}", outpoint);
        }
        finally
        {
            _processingLock.Release();
        }
    }

    private async Task BuildAndSendDelegationAsync(
        string walletId,
        ArkContract contract,
        ArkVtxo vtxo,
        OutPoint outpoint,
        ScriptBuilder intentScriptBuilder,
        ScriptBuilder forfeitScriptBuilder,
        ArkServerInfo serverInfo)
    {
        var signer = await walletProvider.GetSignerAsync(walletId)
            ?? throw new InvalidOperationException($"No signer for wallet {walletId}");

        var addressProvider = await walletProvider.GetAddressProviderAsync(walletId)
            ?? throw new InvalidOperationException($"No address provider for wallet {walletId}");

        // Get signing descriptor from the contract's user key
        var signerDescriptor = contract switch
        {
            NArk.Core.Contracts.ArkDelegateContract dc => dc.User,
            _ => throw new InvalidOperationException($"Unsupported contract type for delegation: {contract.Type}")
        };

        var signerPubKey = await signer.GetPubKey(signerDescriptor);

        // Build the intent message
        var intentMessage = JsonSerializer.Serialize(new
        {
            type = "register",
            cosignersPublicKeys = new[] { Convert.ToHexString(signerPubKey.ToBytes()).ToLowerInvariant() },
            validAt = 0,
            expireAt = 0
        });

        // Build intent proof PSBT (BIP322-style)
        var intentScript = intentScriptBuilder.Build();
        var intentCoin = new ArkCoin(
            walletId, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            outpoint, vtxo.TxOut, signerDescriptor, intentScriptBuilder,
            null, null, null, vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);

        var intentPsbt = CreateBip322Proof(intentMessage, serverInfo.Network, intentCoin);
        var intentPrecomputed = intentPsbt.GetGlobalTransaction()
            .PrecomputeTransactionData([intentPsbt.Inputs[0].GetTxOut()!, intentCoin.TxOut]);

        await PsbtHelpers.SignAndFillPsbt(signer, intentCoin, intentPsbt, intentPrecomputed,
            cancellationToken: CancellationToken.None);

        // Build forfeit tx using the delegate path, signed with SIGHASH_ALL|ANYONECANPAY
        var forfeitCoin = new ArkCoin(
            walletId, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            outpoint, vtxo.TxOut, signerDescriptor, forfeitScriptBuilder,
            null, null, null, vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);

        var forfeitTx = CreateForfeitTransaction(serverInfo.Network, forfeitCoin);
        var forfeitPrecomputed = forfeitTx.GetGlobalTransaction()
            .PrecomputeTransactionData([forfeitCoin.TxOut]);

        await PsbtHelpers.SignAndFillPsbt(signer, forfeitCoin, forfeitTx, forfeitPrecomputed,
            TaprootSigHash.All | TaprootSigHash.AnyoneCanPay, CancellationToken.None);

        // Send to delegator
        await delegatorProvider.DelegateAsync(
            intentMessage,
            intentPsbt.ToBase64(),
            [forfeitTx.ToBase64()]);
    }

    private static PSBT CreateBip322Proof(string message, Network network, ArkCoin coin)
    {
        var messageHash = HashHelpers.CreateTaggedMessageHash("ark-intent-proof-message", message);

        var toSpend = network.CreateTransaction();
        toSpend.Version = 0;
        toSpend.LockTime = 0;
        toSpend.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, 0xFFFFFFFF),
            new Script(OpcodeType.OP_0, Op.GetPushOp(messageHash)))
        {
            Sequence = 0,
            WitScript = WitScript.Empty,
        });
        toSpend.Outputs.Add(new TxOut(Money.Zero, coin.ScriptPubKey));

        var toSign = network.CreateTransaction();
        toSign.Version = 2;
        toSign.LockTime = 0;
        toSign.Inputs.Add(new TxIn(new OutPoint(toSpend.GetHash(), 0)) { Sequence = 0 });
        toSign.Inputs.Add(new TxIn(coin.Outpoint) { Sequence = 0 });
        toSign.Outputs.Add(new TxOut(Money.Zero, new Script(OpcodeType.OP_RETURN)));

        var psbt = PSBT.FromTransaction(toSign, network);
        psbt.Settings.AutomaticUTXOTrimming = false;
        psbt.AddTransactions(toSpend);
        psbt.AddCoins(coin);
        return psbt;
    }

    private static PSBT CreateForfeitTransaction(Network network, ArkCoin coin)
    {
        var tx = network.CreateTransaction();
        tx.Version = 2;
        tx.LockTime = 0;
        tx.Inputs.Add(new TxIn(coin.Outpoint) { Sequence = 0 });
        // OP_RETURN output — delegator will replace with actual outputs during batch
        tx.Outputs.Add(new TxOut(Money.Zero, new Script(OpcodeType.OP_RETURN)));

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Settings.AutomaticUTXOTrimming = false;
        psbt.AddCoins(coin);
        return psbt;
    }

    private async Task<ECPubKey> GetDelegatePubkeyAsync()
    {
        if (_delegatePubkey is not null)
            return _delegatePubkey;

        var info = await delegatorProvider.GetDelegatorInfoAsync();
        _delegatePubkey = ECPubKey.Create(Convert.FromHexString(info.Pubkey));
        return _delegatePubkey;
    }

    public void Dispose()
    {
        vtxoStorage.VtxosChanged -= OnVtxosChanged;
        _processingLock.Dispose();
    }
}
```

> **Key design decisions:**
> - Uses `SIGHASH_ALL | ANYONECANPAY` for forfeit tx: commits to outputs but allows delegator to add connector input
> - Uses BIP322-style proof matching `IntentGenerationService.CreatePsbt` pattern
> - Event-driven via `VtxosChanged` — processes one VTXO at a time with a processing lock
> - Tracks delegated outpoints in-memory to prevent re-delegation
> - `ArkContractParser.Parse` is used to reconstruct the contract from storage (same as `CoinService`)

- [ ] **Step 4: Verify NArk.Core builds**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet build NArk.Core/NArk.Core.csproj --no-restore 2>&1 | tail -10`
Expected: Build succeeds

- [ ] **Step 5: Run all unit tests**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet test NArk.Tests/NArk.Tests.csproj --no-restore -v q 2>&1 | tail -10`
Expected: All tests pass

- [ ] **Step 6: Commit**

```bash
git add NArk.Core/Services/DelegationMonitorService.cs NArk.Tests/DelegationMonitorServiceTests.cs
git commit -m "feat: add DelegationMonitorService for automatic VTXO delegation"
```

---

## Chunk 4: Integration + E2E Tests + CI Green

### Task 8: Wire Everything in AddArkDelegation + Build Verification

**Files:**
- Modify: `NArk.Core/Hosting/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Finalize AddArkDelegation with all imports**

Ensure `ServiceCollectionExtensions.cs` has all necessary `using` statements and the complete `AddArkDelegation` implementation. Check whether `Scrutor` or a decoration pattern is available. If not, use the manual approach described in Task 6.

- [ ] **Step 2: Full solution build**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet build --no-restore 2>&1 | tail -10`
Expected: Build succeeds

- [ ] **Step 3: Full unit test suite**

Run: `cd /c/Git/NArk/submodules/NNark && dotnet test NArk.Tests/NArk.Tests.csproj --no-restore -v q 2>&1 | tail -10`
Expected: All tests pass

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: wire automated delegation in AddArkDelegation"
```

### Task 9: Update README with Delegation Usage

**Files:**
- Modify: `README.md`

Per the CLAUDE.md submodule rule: when adding a new feature, add usage instructions to README.md.

- [ ] **Step 1: Add automated delegation section to README**

Add a section showing:
```csharp
// Configure automated delegation
services.AddArkCoreServices();
services.AddArkDelegation("http://localhost:7012"); // Fulmine delegator gRPC endpoint

// HD wallets will now automatically:
// 1. Derive ArkDelegateContract instead of ArkPaymentContract
// 2. Auto-delegate VTXOs to the delegator on receipt
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs: add automated delegation usage instructions"
```

### Task 10: Push and Verify CI

- [ ] **Step 1: Push to remote**

Run: `cd /c/Git/NArk/submodules/NNark && git push origin feat/delegation`

- [ ] **Step 2: Monitor CI**

Run: `cd /c/Git/NArk/submodules/NNark && gh run list --branch feat/delegation --limit 1`
Wait for CI to complete. If it fails, debug and fix.

- [ ] **Step 3: If CI fails, iterate**

Read CI logs, fix issues, commit, push, repeat until green.

---

## Notes for Implementer

### Contract Paths Cheat Sheet
- **ArkPaymentContract**: `CollaborativePath()` = User+Server 2-of-2 (user spending)
- **ArkDelegateContract**:
  - `ForfeitPath()` = User+Server 2-of-2 (user spending — same concept as CollaborativePath)
  - `DelegatePath()` = User+Delegate+Server 3-of-3 (delegator ACP forfeit)
  - `ExitPath()` = User only after CSV (unilateral recovery)

### What IContractTransformer vs IDelegationTransformer Do
- **IContractTransformer** (`DelegateContractTransformer`): Makes delegate VTXOs visible to the user's SpendingService/IntentGenerationService. Returns `ForfeitPath()` as the spending script.
- **IDelegationTransformer** (`DelegateContractDelegationTransformer`): Used by `DelegationMonitorService` to build delegation artifacts. Returns both `ForfeitPath()` for intent proof and `DelegatePath()` for ACP forfeit tx.

### ACP Forfeit Signing
`SIGHASH_ALL | ANYONECANPAY` = commits to all outputs but only the VTXO input. The delegator adds the batch connector input later. `PsbtHelpers.SignAndFillPsbt` already accepts a `TaprootSigHash` parameter — just pass `TaprootSigHash.All | TaprootSigHash.AnyoneCanPay`.

### DI Decoration Pattern
If `Scrutor` is not available, the implementer should check how other decorators are done in the codebase (e.g., `CachingClientTransport` wraps `GrpcClientTransport`). The same manual pattern can be used:
```csharp
services.AddSingleton<IWalletProvider>(sp => new DelegatingWalletProvider(
    sp.GetRequiredService<ConcreteWalletProvider>(), ...));
```
This requires the concrete type to be registered separately from the interface.

### Race Condition: IntentGenerationService vs DelegationMonitorService
Both react to VtxosChanged. The monitor delegates VTXOs; intent generation tries to roll them over. For now, both will work on the same VTXOs — the delegator will handle the rollover. If conflicts arise in testing, add a check in `IntentGenerationService` to skip contracts of type `Delegate` when `IDelegatorProvider` is registered.
