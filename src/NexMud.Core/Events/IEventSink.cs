using NexMud.Contracts.Events;

namespace NexMud.Core.Events;

public interface IEventSink
{
    ValueTask PublishAsync(IMudEvent mudEvent, string source, CancellationToken cancellationToken = default);
}
