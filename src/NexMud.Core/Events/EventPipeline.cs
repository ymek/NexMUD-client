using System.Threading.Channels;
using NexMud.Contracts.Events;
using NexMud.Contracts.Gameplay;

namespace NexMud.Core.Events;

public sealed class EventPipeline : IEventSink, IAsyncDisposable
{
    private readonly Channel<EventEnvelope> _stateEvents;
    private readonly object _observerLock = new();
    private readonly List<Channel<EventEnvelope>> _observers = [];
    private long _sequence;
    private bool _disposed;

    public EventPipeline()
    {
        _stateEvents = Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public ChannelReader<EventEnvelope> StateEvents => _stateEvents.Reader;

    public ChannelReader<EventEnvelope> SubscribeLossless()
    {
        Channel<EventEnvelope> channel = Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        lock (_observerLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _observers.Add(channel);
        }

        return channel.Reader;
    }

    public ChannelReader<EventEnvelope> Subscribe(int capacity = 512)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Channel<EventEnvelope> channel = Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });

        lock (_observerLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _observers.Add(channel);
        }

        return channel.Reader;
    }

    public ValueTask PublishAsync(
        IMudEvent mudEvent,
        string source,
        CancellationToken cancellationToken = default) =>
        PublishCoreAsync(
            mudEvent,
            source,
            DateTimeOffset.UtcNow,
            SessionId.Empty,
            null,
            cancellationToken);

    public ValueTask PublishSemanticAsync(
        IMudEvent mudEvent,
        string source,
        SessionId sessionId,
        long sourceSequence,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default) =>
        PublishCoreAsync(
            mudEvent,
            source,
            observedAt,
            sessionId,
            sourceSequence,
            cancellationToken);

    private ValueTask PublishCoreAsync(
        IMudEvent mudEvent,
        string source,
        DateTimeOffset timestamp,
        SessionId sessionId,
        long? sourceSequence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mudEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_observerLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            EventEnvelope envelope = new(
                Guid.NewGuid(),
                ++_sequence,
                timestamp,
                source,
                mudEvent)
            {
                SessionId = sessionId,
                SourceSequence = sourceSequence
            };

            if (!_stateEvents.Writer.TryWrite(envelope))
            {
                throw new InvalidOperationException("Authoritative event channel is not accepting events.");
            }

            foreach (Channel<EventEnvelope> observer in _observers)
            {
                observer.Writer.TryWrite(envelope);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_observerLock)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _stateEvents.Writer.TryComplete();
            foreach (Channel<EventEnvelope> observer in _observers)
            {
                observer.Writer.TryComplete();
            }
            _observers.Clear();
        }

        return ValueTask.CompletedTask;
    }
}
