using JevMud.Contracts.State;

namespace JevMud.Client.Presentation;

public readonly record struct CommandInputPresentation(
    bool Sensitive,
    char PasswordChar,
    string Placeholder,
    bool AllowHistory,
    bool AllowCompletion,
    bool ClearAfterSubmit);

public readonly record struct CommandInputTransitionPresentation(
    bool ClearInput,
    bool Refocus);

/// <summary>
/// Deterministic command-input behavior for normal and protocol-driven input modes.
/// Keeping this outside the GUI makes credential transitions, masking and history exclusion testable.
/// </summary>
public static class CommandInputPolicy
{
    public static CommandInputPresentation For(SessionInputMode mode) => mode switch
    {
        SessionInputMode.LoginPassword => new(
            Sensitive: true,
            PasswordChar: '●',
            Placeholder: "Password",
            AllowHistory: false,
            AllowCompletion: false,
            ClearAfterSubmit: true),
        SessionInputMode.LoginName => new(
            Sensitive: false,
            PasswordChar: '\0',
            Placeholder: "Character name",
            AllowHistory: false,
            AllowCompletion: false,
            ClearAfterSubmit: true),
        SessionInputMode.Pager => new(
            Sensitive: false,
            PasswordChar: '\0',
            Placeholder: "Press Enter to continue",
            AllowHistory: false,
            AllowCompletion: false,
            ClearAfterSubmit: true),
        SessionInputMode.Editor => new(
            Sensitive: false,
            PasswordChar: '\0',
            Placeholder: "Editor input",
            AllowHistory: false,
            AllowCompletion: true,
            ClearAfterSubmit: false),
        _ => new(
            Sensitive: false,
            PasswordChar: '\0',
            Placeholder: "Enter a MUD command",
            AllowHistory: true,
            AllowCompletion: true,
            ClearAfterSubmit: false)
    };

    public static CommandInputTransitionPresentation Transition(SessionInputMode previous, SessionInputMode current)
    {
        if (previous == current)
        {
            return new CommandInputTransitionPresentation(ClearInput: false, Refocus: false);
        }

        bool credentialBoundary = previous is SessionInputMode.LoginName or SessionInputMode.LoginPassword ||
                                  current is SessionInputMode.LoginName or SessionInputMode.LoginPassword;
        return new CommandInputTransitionPresentation(
            ClearInput: credentialBoundary,
            Refocus: true);
    }

    public static bool ShouldRecordHistory(SessionInputMode mode, string input) =>
        input.Length > 0 && For(mode).AllowHistory;

    public static bool ShouldEchoToTranscript(bool sensitive) => !sensitive;
}
