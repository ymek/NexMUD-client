using Jint;
using JevMud.Scripting.Runtime;
using JevMud.Scripting.Scheduling;

namespace JevMud.Scripting.Jint.Runtime;

/// <summary>
/// The single construction point for production Jint engines. General CLR access is deliberately
/// never enabled here; scripts see only the narrow delegates installed by JintHostBridge.
/// </summary>
public sealed class JintEngineFactory
{
    public Engine Create(
        ScriptResourceLimits limits,
        CancellationToken cancellationToken,
        IScriptScheduler clock)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(clock);
        return new Engine(options =>
        {
            options.Strict();
            options.DisableStringCompilation();
            options.TimeZone = TimeZoneInfo.Utc;
            options.TimeSystem = new NexMudTimeSystem(clock);
            options.LimitMemory(limits.MaximumMemoryBytes);
            options.TimeoutInterval(limits.ExecutionTimeout);
            options.MaxStatements(limits.MaximumStatements);
            options.CancellationToken(cancellationToken);
            options.LimitRecursion(limits.MaximumRecursionDepth);
            options.Constraints.MaxExecutionStackCount = limits.MaximumExecutionStackDepth;
            options.RegexTimeoutInterval(limits.RegexTimeout);
            options.MaxArraySize((uint) limits.MaximumArraySize);
            options.MaxJsonParseDepth(Math.Max(8, Math.Min(limits.MaximumRecursionDepth, 256)));

            // CLR interop is opt-in in Jint. Keep every general interop grant closed. Explicit
            // delegates installed by the host bridge are the only crossing into C#.
            options.Interop.Enabled = false;
            options.Interop.AllowGetType = false;
            options.Interop.AllowSystemReflection = false;
            options.Interop.AllowWrite = false;
            options.Interop.AllowOperatorOverloading = false;
            options.Interop.ChainClrExceptionAsInnerException = false;
            options.AgentCanSuspend = false;
            options.Modules.RegisterRequire = false;
            options.Constraints.PromiseTimeout = limits.PromiseTimeout;
            options.Constraints.StackOverflowGuard = true;
        });
    }
}
