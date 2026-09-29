using JevMud.Scripting.Events;
using JevMud.Scripting.Permissions;
using JevMud.Scripting.Runtime;
using JevMud.Scripting.Scheduling;
using JevMud.Scripting.Storage;

namespace JevMud.Scripting.Host;

public enum ScriptCommandOrigin
{
    User,
    Automation,
    Jev,
    Mapper,
    Script,
    System
}

public sealed record ScriptCommandRequest(
    string Command,
    ScriptCommandOrigin Origin,
    string OwnerId,
    string OwnerName,
    string? Reason = null,
    bool Sensitive = false,
    long? ExpectedStateVersion = null,
    Guid? ActionId = null,
    string? Domain = null,
    Guid? DecisionId = null,
    bool UserApproved = true,
    string? ModuleId = null,
    string? ScriptVersion = null,
    Guid? ScriptInstanceId = null,
    Guid? InvocationId = null,
    Guid? EventId = null,
    Guid? ParentOperationId = null,
    string? AutomationId = null,
    string? AutomationType = null,
    string? TriggerId = null,
    Guid? RouteExecutionId = null,
    string? RouteId = null,
    int? RouteStep = null,
    int? RouteTotalSteps = null);

public sealed record ScriptCommandResult(Guid ActionId, bool Accepted, string? Reason = null);

public sealed record ScriptResourceState(int? Current, int? Maximum, double? Percent);

public sealed record ScriptCharacterState(
    ScriptResourceState Health,
    ScriptResourceState Mana,
    ScriptResourceState Movement,
    string? Position);

public sealed record ScriptRoomState(
    string? Id,
    string? Name,
    IReadOnlyList<string> Exits);

public sealed record ScriptCombatState(bool Active, string? Target);

public sealed record ScriptStateSnapshot(
    long Version,
    bool Connected,
    string InputMode,
    ScriptCharacterState Character,
    ScriptRoomState Room,
    ScriptCombatState Combat);

public sealed record ScriptRoomSnapshot(string Id, string? Name);

public sealed record ScriptRouteStep(
    int Sequence,
    string FromRoomId,
    string Direction,
    string ExpectedRoomId,
    string? DoorState,
    string? Traversability,
    string? RecoveryCommand = null);

public sealed record ScriptRoutePlan(
    string RouteId,
    string StartRoomId,
    string DestinationRoomId,
    long GraphVersion,
    IReadOnlyList<ScriptRouteStep> Steps);

public enum ScriptMovementFailureReason
{
    NoExit,
    ClosedDoor,
    LockedDoor,
    CannotMove,
    CombatRestriction,
    StandingRequired,
    Unknown
}

public sealed record ScriptMoveRequest(
    string Direction,
    string? FromRoomId = null,
    string? ExpectedRoomId = null,
    Guid? RouteExecutionId = null,
    string? RouteId = null,
    int? RouteStep = null,
    int? RouteTotalSteps = null,
    string? Recovery = null);

public sealed record ScriptMovementResult(
    string Kind,
    string? FromRoomId = null,
    string? ToRoomId = null,
    string? ExpectedRoomId = null,
    string? ActualRoomId = null,
    ScriptMovementFailureReason? Reason = null,
    string? Message = null,
    string? RecoveryCommand = null,
    bool ReplanSuggested = false);

public sealed record ScriptCodexResult(string Kind, string Key, string Title, string? Subtitle);

public enum ScriptUiNotificationLevel { Information, Success, Warning, Error }

public sealed record ScriptUiNotification(
    string Title,
    string Message,
    ScriptUiNotificationLevel Level = ScriptUiNotificationLevel.Information);

public enum ScriptLogLevel { Trace, Debug, Information, Warning, Error }

public interface IScriptCommands
{
    Task<ScriptCommandResult> SendAsync(ScriptCommandRequest request, CancellationToken cancellationToken = default);
}

public interface IScriptState
{
    ScriptStateSnapshot Snapshot();
}

public interface IScriptMapper
{
    Task<ScriptRoomSnapshot?> CurrentRoomAsync(CancellationToken cancellationToken = default);
    Task<ScriptRoutePlan?> FindPathAsync(string destinationRoomId, CancellationToken cancellationToken = default);
    Task<ScriptMovementResult> MoveAsync(ScriptMoveRequest request, CancellationToken cancellationToken = default);
}

public interface IScriptCodex
{
    Task<IReadOnlyList<ScriptCodexResult>> SearchAsync(string query, int limit = 50, CancellationToken cancellationToken = default);
}

public interface IScriptUi
{
    Task NotifyAsync(ScriptUiNotification notification, CancellationToken cancellationToken = default);
}

public interface IScriptLog
{
    Task WriteAsync(ScriptLogLevel level, string message, CancellationToken cancellationToken = default);

    Task WriteAsync(
        ScriptLogLevel level,
        string message,
        string? dataJson,
        CancellationToken cancellationToken = default) =>
        WriteAsync(level, message, cancellationToken);
}

public interface IScriptHost
{
    ScriptModuleId ModuleId { get; }
    IScriptPermissionSet Permissions { get; }
    IScriptEvents Events { get; }
    IScriptCommands Commands { get; }
    IScriptState State { get; }
    IScriptMapper Mapper { get; }
    IScriptCodex Codex { get; }
    IScriptStorage Storage { get; }
    IScriptScheduler Timers { get; }
    IScriptUi Ui { get; }
    IScriptLog Log { get; }
}
