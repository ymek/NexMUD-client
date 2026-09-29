using JevMud.Contracts.Jev;

namespace JevMud.Contracts.Actions;

public interface IMudAction;

public sealed record SendCommandAction(string Command, bool Sensitive = false) : IMudAction;

public enum CommandOrigin
{
    User,
    Automation,
    Jev,
    Mapper,
    Script,
    System
}

public sealed record CommandProvenance(
    CommandOrigin Origin,
    string OwnerId,
    string OwnerName,
    string? Reason = null,
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

public sealed record MudActionEnvelope(
    Guid ActionId,
    Guid? DecisionId,
    long StateVersion,
    JevDomain? Domain,
    DecisionSource Source,
    bool UserApproved,
    DateTimeOffset CreatedAt,
    IMudAction Action,
    CommandProvenance? Provenance = null);
