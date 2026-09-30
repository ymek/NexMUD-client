using NexMud.Scripting.Host;
using NexMud.Scripting.Runtime;

namespace NexMud.Client.Scripting;

/// <summary>
/// Resolves Automation/manual function calls against the already-running package runtime for the
/// active Connection Profile. It never creates a Jint engine per invocation.
/// </summary>
public sealed class ClientScriptFunctionInvoker : IScriptFunctions
{
    private readonly IJavaScriptRuntime _runtime;
    private readonly Func<string> _activeProfileId;

    public ClientScriptFunctionInvoker(IJavaScriptRuntime runtime, Func<string> activeProfileId)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _activeProfileId = activeProfileId ?? throw new ArgumentNullException(nameof(activeProfileId));
    }

    public Task<ScriptFunctionInvocationResult> InvokeAsync(
        ScriptFunctionInvocationDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string profileId = _activeProfileId();
        AutomationInvocationContext context = new(
            Guid.NewGuid(),
            profileId,
            descriptor.SourceKind,
            descriptor.SourceDefinitionId,
            DateTimeOffset.UtcNow,
            descriptor.Arguments,
            descriptor.Captures,
            descriptor.SemanticEvent,
            cancellationToken);
        return _runtime.InvokeExportAsync(
            new ScriptFunctionInvocationRequest(
                descriptor.FunctionRef,
                context,
                descriptor.RequireBooleanResult,
                descriptor.ParentInvocationId),
            cancellationToken);
    }
}
