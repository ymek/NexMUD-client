using NexMud.Contracts.Gameplay;

namespace NexMud.Contracts.Events;

public interface IMudEvent;

public sealed record EventEnvelope(
    Guid EventId,
    long Sequence,
    DateTimeOffset Timestamp,
    string Source,
    IMudEvent Payload,
    Guid? CorrelationId = null,
    Guid? CausationId = null)
{
    public SessionId SessionId { get; init; } = NexMud.Contracts.Gameplay.SessionId.Empty;
    public long? SourceSequence { get; init; }
    public DateTimeOffset ObservedAt => Timestamp;
}
