using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Core.Extensions;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Contracts;

public class ArkDelegateContract : ArkContract
{
    private readonly Sequence _exitDelay;
    public OutputDescriptor User { get; }
    public OutputDescriptor Delegate { get; }
    public LockTime CltvLocktime { get; }

    public override string Type => ContractType;
    public const string ContractType = "Delegate";

    public ArkDelegateContract(
        OutputDescriptor server,
        Sequence exitDelay,
        OutputDescriptor user,
        OutputDescriptor @delegate,
        LockTime cltvLocktime)
        : base(server)
    {
        _exitDelay = exitDelay;
        CltvLocktime = cltvLocktime;
        User = user;
        Delegate = @delegate;
    }

    protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        return [
            ForfeitPath(),
            ExitPath(),
            DelegatePath()
        ];
    }

    /// <summary>
    /// Collaborative forfeit path: user + server (2-of-2).
    /// </summary>
    public ScriptBuilder ForfeitPath()
    {
        var ownerScript = new NofNMultisigTapScript([User.ToXOnlyPubKey()]);
        return new CollaborativePathArkTapScript(Server!.ToXOnlyPubKey(), ownerScript);
    }

    /// <summary>
    /// Unilateral exit path: user only, after CSV delay.
    /// </summary>
    public ScriptBuilder ExitPath()
    {
        var ownerScript = new NofNMultisigTapScript([User.ToXOnlyPubKey()]);
        return new UnilateralPathArkTapScript(_exitDelay, ownerScript);
    }

    /// <summary>
    /// Delegate path: user + delegate + server (3-of-3), gated by CLTV locktime.
    /// </summary>
    public ScriptBuilder DelegatePath()
    {
        var multisig = new NofNMultisigTapScript([User.ToXOnlyPubKey(), Delegate.ToXOnlyPubKey()]);
        var cltvGate = new LockTimeTapScript(CltvLocktime);
        return new CollaborativePathArkTapScript(Server!.ToXOnlyPubKey(),
            new CompositeTapScript(cltvGate, multisig));
    }

    protected override Dictionary<string, string> GetContractData()
    {
        return new Dictionary<string, string>
        {
            ["exit_delay"] = _exitDelay.Value.ToString(),
            ["user"] = User.ToString(),
            ["delegate"] = Delegate.ToString(),
            ["server"] = Server!.ToString(),
            ["cltv_locktime"] = CltvLocktime.Value.ToString()
        };
    }

    public static ArkContract Parse(Dictionary<string, string> contractData, Network network)
    {
        var server = KeyExtensions.ParseOutputDescriptor(contractData["server"], network);
        var exitDelay = new Sequence(uint.Parse(contractData["exit_delay"]));
        var userDescriptor = KeyExtensions.ParseOutputDescriptor(contractData["user"], network);
        var delegateDescriptor = KeyExtensions.ParseOutputDescriptor(contractData["delegate"], network);
        var cltvLocktime = new LockTime(uint.Parse(contractData["cltv_locktime"]));
        return new ArkDelegateContract(server, exitDelay, userDescriptor, delegateDescriptor, cltvLocktime);
    }
}
