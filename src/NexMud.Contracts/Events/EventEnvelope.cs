namespace NexMud.Contracts.Events;

public interface IMudEvent;

public sealed record EventEnvelope(
    Guid EventId,
    long Sequence,
    DateTimeOffset Timestamp,
    string Source,
    IMudEvent Payload,
    Guid? CorrelationId = null,
    Guid? CausationId = null);
