using JevMud.Client.Automation;

namespace JevMud.Client.Interaction;

/// <summary>
/// Application-boundary adapter between typed input processing and Automation alias matching.
/// Alias execution remains owned by Automation/Jint; the input pipeline owns ordering and consumption.
/// </summary>
public sealed class AutomationAliasResolver : IInputAliasResolver
{
    private readonly AutomationRuntimeCompiler _automation;
    private readonly Func<IReadOnlyDictionary<string, string>> _variables;

    public AutomationAliasResolver(
        AutomationRuntimeCompiler automation,
        Func<IReadOnlyDictionary<string, string>> variables)
    {
        _automation = automation;
        _variables = variables;
    }

    public Task<bool> TryResolveAsync(string command, CancellationToken cancellationToken = default) =>
        _automation.TryHandleAliasAsync(command, _variables(), cancellationToken);
}
