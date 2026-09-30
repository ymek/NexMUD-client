using NexMud.Contracts.Events;
using NexMud.Contracts.Gameplay;

namespace NexMud.Core.Events;

public interface IEventSink
{
    ValueTask PublishAsync(IMudEvent mudEvent, string source, CancellationToken cancellationToken = default);

    ValueTask PublishSemanticAsync(
        IMudEvent mudEvent,
        string source,
        SessionId sessionId,
        long sourceSequence,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);
}
