namespace NexMud.Scripting.Runtime;

public enum ScriptRuntimeProfile
{
    InternalTrusted,
    GeneratedAutomation,
    UserScript,
    FutureThirdPartyPlugin
}

public sealed record ScriptResourceLimits(
    TimeSpan ExecutionTimeout,
    int MaximumStatements,
    long MaximumMemoryBytes,
    int MaximumRecursionDepth,
    int MaximumExecutionStackDepth,
    TimeSpan RegexTimeout,
    TimeSpan PromiseTimeout,
    int MaximumArraySize,
    int MaximumSourceLength,
    int MaximumAstNodes,
    int MaximumModuleCount,
    long MaximumTotalModuleSourceBytes,
    int MaximumModuleGraphDepth,
    int MaximumModuleResolutionHops,
    int MailboxCapacity)
{
    public static ScriptResourceLimits For(ScriptRuntimeProfile profile) => profile switch
    {
        ScriptRuntimeProfile.InternalTrusted => new(
            TimeSpan.FromSeconds(2), 1_000_000, 64L * 1024 * 1024, 256, 512,
            TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5), 1_000_000,
            2_000_000, 500_000, 128, 8L * 1024 * 1024, 64, 256, 1024),
        ScriptRuntimeProfile.GeneratedAutomation => new(
            TimeSpan.FromMilliseconds(500), 200_000, 16L * 1024 * 1024, 128, 256,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(3), 250_000,
            512_000, 125_000, 48, 2L * 1024 * 1024, 24, 96, 512),
        ScriptRuntimeProfile.UserScript => new(
            TimeSpan.FromMilliseconds(250), 100_000, 8L * 1024 * 1024, 96, 192,
            TimeSpan.FromMilliseconds(75), TimeSpan.FromSeconds(2), 100_000,
            256_000, 75_000, 32, 1L * 1024 * 1024, 16, 64, 256),
        ScriptRuntimeProfile.FutureThirdPartyPlugin => new(
            TimeSpan.FromMilliseconds(150), 50_000, 4L * 1024 * 1024, 64, 128,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1), 50_000,
            128_000, 40_000, 16, 512L * 1024, 12, 32, 128),
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };
}

public static class ScriptApiVersion
{
    public const string Current = "1";

    public static bool IsCompatible(string requested) =>
        string.Equals(requested?.Trim(), Current, StringComparison.Ordinal);
}

public enum ScriptInstanceState
{
    Created,
    Compiled,
    Loading,
    Running,
    Faulted,
    Stopping,
    Stopped,
    Disposed
}
