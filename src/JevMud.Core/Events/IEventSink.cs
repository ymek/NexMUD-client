using JevMud.Contracts.Events;

namespace JevMud.Core.Events;

public interface IEventSink
{
    ValueTask PublishAsync(IMudEvent mudEvent, string source, CancellationToken cancellationToken = default);
}
