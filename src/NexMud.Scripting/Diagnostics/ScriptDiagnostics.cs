using NexMud.Scripting.Runtime;

namespace NexMud.Scripting.Diagnostics;

public enum ScriptErrorKind
{
    CompileError,
    LoadError,
    PermissionError,
    ScriptRuntimeError,
    ScriptTimeoutError,
    ScriptResourceLimitError,
    HostOperationError,
    CancelledError,
    EngineFault
}

public enum ScriptDiagnosticKind
{
    ScriptLoaded,
    ScriptUnloaded,
    HandlerInvoked,
    InvocationStarted,
    InvocationCompleted,
    InvocationFaulted,
    InvocationCancelled,
    HostRequestStarted,
    HostRequestCompleted,
    CommandEmitted,
    PermissionDenied,
    Timeout,
    ResourceLimitExceeded,
    UncaughtException,
    ReloadCompleted,
    QueueOverflow
}

public sealed record ScriptSourceLocation(
    string? SourceFile,
    int? Line,
    int? Column,
    string? GeneratedFile = null,
    int? GeneratedLine = null,
    int? GeneratedColumn = null);

public sealed record ScriptDiagnosticRecord(
    ScriptDiagnosticKind Kind,
    DateTimeOffset Timestamp,
    ScriptModuleId ScriptId,
    string ScriptVersion,
    Guid ScriptInstanceId,
    Guid? InvocationId = null,
    Guid? EventId = null,
    Guid? OperationId = null,
    Guid? ParentOperationId = null,
    ScriptSourceLocation? Location = null,
    TimeSpan? Duration = null,
    string? Result = null,
    string? Message = null);

public sealed record ScriptError(
    ScriptErrorKind Kind,
    string Code,
    string Message,
    ScriptSourceLocation? Location = null);

public interface IScriptDiagnosticsSink
{
    void Record(ScriptDiagnosticRecord diagnostic);
}

public sealed class NullScriptDiagnosticsSink : IScriptDiagnosticsSink
{
    public static NullScriptDiagnosticsSink Instance { get; } = new();
    private NullScriptDiagnosticsSink() { }
    public void Record(ScriptDiagnosticRecord diagnostic) { }
}
