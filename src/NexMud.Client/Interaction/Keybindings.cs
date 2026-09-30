using NexMud.Client.Settings;

namespace NexMud.Client.Interaction;

public enum KeybindingContext
{
    Global,
    World,
    Input,
    Mapper,
    ScriptEditor,
    Dialog
}

public enum KeybindingActionKind
{
    SubmitInput,
    HistoryPrevious,
    HistoryNext,
    CompletionNext,
    CompletionPrevious,
    ClearInput,
    FocusInput,
    ScrollPageUp,
    ScrollPageDown,
    ScrollToBottom,
    SearchScrollback,
    SendCommand,
    RunAutomation,
    RunScriptFunction,
    TogglePane,
    ToggleJev,
    MapperPause,
    MapperResume,
    MapperAbort
}

public sealed record KeybindingResolution(CommandKeyBinding? Binding, bool HasConflict)
{
    public static KeybindingResolution None { get; } = new(null, false);
}

/// <summary>Context-aware deterministic keybinding resolution independent of Avalonia controls.</summary>
public sealed class KeybindingService
{
    private IReadOnlyList<CommandKeyBinding> _bindings = Array.Empty<CommandKeyBinding>();

    public void Configure(IEnumerable<CommandKeyBinding>? bindings)
    {
        _bindings = (bindings ?? Array.Empty<CommandKeyBinding>())
            .Where(binding => binding.Enabled && !string.IsNullOrWhiteSpace(binding.Gesture))
            .ToArray();
    }

    public KeybindingResolution Resolve(KeybindingContext context, Func<CommandKeyBinding, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        CommandKeyBinding[] exact = _bindings
            .Where(binding => binding.Context == context && matches(binding))
            .OrderByDescending(binding => binding.Priority)
            .ToArray();
        KeybindingResolution resolution = ResolveTier(exact);
        if (resolution.Binding is not null || resolution.HasConflict || context == KeybindingContext.Global)
            return resolution;

        return ResolveTier(_bindings
            .Where(binding => binding.Context == KeybindingContext.Global && matches(binding))
            .OrderByDescending(binding => binding.Priority)
            .ToArray());
    }

    public KeybindingResolution Resolve(string gesture, KeybindingContext context)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        CommandKeyBinding[] exact = _bindings
            .Where(binding => string.Equals(binding.Gesture, gesture, StringComparison.OrdinalIgnoreCase) &&
                              binding.Context == context)
            .OrderByDescending(binding => binding.Priority)
            .ToArray();
        KeybindingResolution resolution = ResolveTier(exact);
        if (resolution.Binding is not null || resolution.HasConflict || context == KeybindingContext.Global)
            return resolution;

        return ResolveTier(_bindings
            .Where(binding => string.Equals(binding.Gesture, gesture, StringComparison.OrdinalIgnoreCase) &&
                              binding.Context == KeybindingContext.Global)
            .OrderByDescending(binding => binding.Priority)
            .ToArray());
    }

    public IReadOnlyList<IReadOnlyList<CommandKeyBinding>> Conflicts() =>
        _bindings
            .GroupBy(binding => (Gesture: binding.Gesture.ToUpperInvariant(), binding.Context, binding.Priority))
            .Where(group => group.Count() > 1)
            .Select(group => (IReadOnlyList<CommandKeyBinding>)group.ToArray())
            .ToArray();

    private static KeybindingResolution ResolveTier(IReadOnlyList<CommandKeyBinding> bindings)
    {
        if (bindings.Count == 0) return KeybindingResolution.None;
        int priority = bindings[0].Priority;
        CommandKeyBinding[] winners = bindings.Where(binding => binding.Priority == priority).ToArray();
        return winners.Length == 1
            ? new KeybindingResolution(winners[0], false)
            : new KeybindingResolution(null, true);
    }
}
