using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Wallet;
using NBitcoin;
using NBitcoin.Scripting;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class DelegatingAddressProviderTests
{
    private static readonly OutputDescriptor ServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private static readonly OutputDescriptor UserKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
            Network.RegTest);

    private static readonly OutputDescriptor DelegateKey =
        KeyExtensions.ParseOutputDescriptor(
            "021e1bb85455fe3f5aed60d101aa4dbdb9e7714f6226769a97a17a5331dadcd53b",
            Network.RegTest);

    private static readonly Sequence ExitDelay = new(144);

    [Test]
    public async Task GetNextContract_Receive_ReturnsDelegateContract()
    {
        var paymentContract = new ArkPaymentContract(ServerKey, ExitDelay, UserKey);
        var entity = paymentContract.ToEntity("wallet-1");
        var inner = Substitute.For<IArkadeAddressProvider>();
        inner.GetNextContract(NextContractPurpose.Receive, ContractActivityState.Active, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(ArkContract, ArkContractEntity)>((paymentContract, entity)));

        var provider = new DelegatingAddressProvider(inner, DelegateKey, ServerKey, ExitDelay);

        var (contract, resultEntity) = await provider.GetNextContract(NextContractPurpose.Receive, ContractActivityState.Active);

        Assert.That(contract, Is.InstanceOf<ArkDelegateContract>());
        var dc = (ArkDelegateContract)contract;
        Assert.That(dc.User, Is.EqualTo(UserKey));
        Assert.That(dc.Delegate, Is.EqualTo(DelegateKey));
        Assert.That(resultEntity.WalletIdentifier, Is.EqualTo("wallet-1"));
    }

    [Test]
    public async Task GetNextContract_SendToSelf_ReturnsDelegateContract()
    {
        var paymentContract = new ArkPaymentContract(ServerKey, ExitDelay, UserKey);
        var entity = paymentContract.ToEntity("wallet-1", activityState: ContractActivityState.AwaitingFundsBeforeDeactivate);
        var inner = Substitute.For<IArkadeAddressProvider>();
        inner.GetNextContract(NextContractPurpose.SendToSelf, Arg.Any<ContractActivityState>(), Arg.Any<ArkContract[]?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(ArkContract, ArkContractEntity)>((paymentContract, entity)));

        var provider = new DelegatingAddressProvider(inner, DelegateKey, ServerKey, ExitDelay);

        var (contract, _) = await provider.GetNextContract(NextContractPurpose.SendToSelf, ContractActivityState.Active);

        Assert.That(contract, Is.InstanceOf<ArkDelegateContract>());
    }

    [Test]
    public async Task GetNextContract_Boarding_PassesThrough()
    {
        var boardingContract = new ArkBoardingContract(ServerKey, ExitDelay, UserKey);
        var entity = boardingContract.ToEntity("wallet-1");
        var inner = Substitute.For<IArkadeAddressProvider>();
        inner.GetNextContract(NextContractPurpose.Boarding, ContractActivityState.Active, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(ArkContract, ArkContractEntity)>((boardingContract, entity)));

        var provider = new DelegatingAddressProvider(inner, DelegateKey, ServerKey, ExitDelay);

        var (contract, _) = await provider.GetNextContract(NextContractPurpose.Boarding, ContractActivityState.Active);

        Assert.That(contract, Is.InstanceOf<ArkBoardingContract>());
    }

    [Test]
    public async Task IsOurs_DelegatesToInner()
    {
        var inner = Substitute.For<IArkadeAddressProvider>();
        inner.IsOurs(UserKey, Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));

        var provider = new DelegatingAddressProvider(inner, DelegateKey, ServerKey, ExitDelay);

        Assert.That(await provider.IsOurs(UserKey), Is.True);
    }

    [Test]
    public async Task GetNextContract_NonPaymentContract_PassesThrough()
    {
        // When inner returns a non-payment contract (e.g., UnknownArkContract for sweep),
        // it should pass through unchanged
        var delegateContract = new ArkDelegateContract(ServerKey, ExitDelay, UserKey, DelegateKey);
        var entity = delegateContract.ToEntity("wallet-1");
        var inner = Substitute.For<IArkadeAddressProvider>();
        inner.GetNextContract(NextContractPurpose.Receive, ContractActivityState.Active, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(ArkContract, ArkContractEntity)>((delegateContract, entity)));

        var provider = new DelegatingAddressProvider(inner, DelegateKey, ServerKey, ExitDelay);

        var (contract, _) = await provider.GetNextContract(NextContractPurpose.Receive, ContractActivityState.Active);

        // Should NOT double-wrap — delegate contract passes through
        Assert.That(contract, Is.SameAs(delegateContract));
    }
}
