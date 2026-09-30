using System.Text.Json;
using System.Text.Json.Serialization;
using NexMud.Scripting.Diagnostics;

namespace NexMud.Scripting.Runtime;

public sealed record ScriptFunctionRef(
    string PackageId,
    string ModulePath,
    string ExportName)
{
    public string PackageId { get; } = Require(PackageId, nameof(PackageId));
    public string ModulePath { get; } = NormalizePath(ModulePath);
    public string ExportName { get; } = Require(ExportName, nameof(ExportName));

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string NormalizePath(string value)
    {
        string path = Require(value, nameof(ModulePath)).Replace('\\', '/').TrimStart('/');
        if (path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Script module paths must be package-relative and may not traverse directories.", nameof(ModulePath));
        return path;
    }
}

public enum AutomationInvocationSourceKind
{
    Alias,
    Keybinding,
    TextTrigger,
    SemanticTrigger,
    Timer,
    StateRule,
    Workflow,
    ManualScriptRun
}

public enum ScriptFunctionFailurePolicy
{
    StopCurrentAutomation,
    ContinueCurrentAutomation
}

public sealed record AutomationInvocationContext(
    Guid InvocationId,
    string ProfileId,
    AutomationInvocationSourceKind SourceKind,
    string SourceDefinitionId,
    DateTimeOffset StartedAt,
    JsonElement Arguments,
    IReadOnlyDictionary<string, string>? Captures = null,
    JsonElement? SemanticEvent = null,
    [property: JsonIgnore] CancellationToken Cancellation = default)
{
    public string ProfileId { get; } = Require(ProfileId, nameof(ProfileId));
    public string SourceDefinitionId { get; } = Require(SourceDefinitionId, nameof(SourceDefinitionId));
    public IReadOnlyDictionary<string, string> Captures { get; } =
        Captures ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

public sealed record ScriptFunctionInvocationDescriptor(
    ScriptFunctionRef FunctionRef,
    AutomationInvocationSourceKind SourceKind,
    string SourceDefinitionId,
    JsonElement Arguments,
    IReadOnlyDictionary<string, string>? Captures = null,
    JsonElement? SemanticEvent = null,
    bool RequireBooleanResult = false,
    Guid? ParentInvocationId = null);

public sealed record ScriptFunctionInvocationRequest(
    ScriptFunctionRef FunctionRef,
    AutomationInvocationContext Context,
    bool RequireBooleanResult = false,
    Guid? ParentAutomationInvocationId = null);

public sealed record ScriptFunctionInvocationResult(
    bool Success,
    JsonElement? Value = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static ScriptFunctionInvocationResult Failed(string code, string message) =>
        new(false, null, code, message);
}

public sealed record ScriptFunctionParameter(
    string Name,
    string Type,
    bool Optional = false);

public sealed record ExportedScriptFunction(
    ScriptFunctionRef FunctionRef,
    string DisplayName,
    string ModulePath,
    string ExportName,
    IReadOnlyList<ScriptFunctionParameter> Parameters,
    string ReturnType,
    string? Documentation,
    ScriptSourceLocation SourceLocation);

public interface IScriptFunctionInvoker
{
    Task<ScriptFunctionInvocationResult> InvokeAsync(
        ScriptFunctionInvocationDescriptor descriptor,
        CancellationToken cancellationToken = default);
}
