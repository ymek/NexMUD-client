using System.Collections.ObjectModel;
using System.Threading.Channels;
using NexMud.Contracts.Actions;
using NexMud.Contracts.Events;
using NexMud.Contracts.Gameplay;

namespace NexMud.Client.Commands;

/// <summary>
/// Bounded evidence journal for commands sent to the MUD. Prompt arrival is never
/// interpreted as command completion or acknowledgement.
/// </summary>
public sealed class OutboundCommandJournal
{
    private readonly ChannelReader<EventEnvelope> _events;
    private readonly object _sync = new();
    private readonly List<OutboundCommandRecord> _records = [];
    private readonly int _capacity;
    private SessionId _sessionId = SessionId.Empty;
    private long? _lastSourceSequence;

    public OutboundCommandJournal(ChannelReader<EventEnvelope> events, int capacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _events = events;
        _capacity = capacity;
    }

    public IReadOnlyList<OutboundCommandRecord> Snapshot()
    {
        lock (_sync)
        {
            return new ReadOnlyCollection<OutboundCommandRecord>(_records.ToArray());
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (EventEnvelope envelope in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (envelope.Payload)
                {
                    case GameObservationReceived observed:
                        ObserveSource(observed.Observation);
                        break;
                    case ActionDispatching dispatch:
                        RecordDispatch(envelope, dispatch);
                        break;
                    case ActionExecuted executed:
                        UpdateState(executed.ActionId, OutboundCommandState.TransportWritten);
                        break;
                    case GameCommandQueueCleared cleared:
                        MarkQueueCleared(cleared.SourceSequence);
                        break;
                    case ConnectionStateChanged { Status: ConnectionStatus.Connected }:
                        BeginSession(SessionId.Empty);
                        break;
                    case ConnectionStateChanged { Status: ConnectionStatus.Disconnected }:
                        MarkSessionEnded();
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ObserveSource(GameObservation observation)
    {
        lock (_sync)
        {
            if (_sessionId != observation.SessionId)
            {
                _records.Clear();
                _sessionId = observation.SessionId;
            }
            _lastSourceSequence = observation.Sequence;
        }
    }

    private void RecordDispatch(EventEnvelope envelope, ActionDispatching dispatch)
    {
        CommandOrigin origin = dispatch.Provenance?.Origin ?? CommandOrigin.User;
        string text = dispatch.Sensitive ? "<redacted>" : dispatch.Command;
        lock (_sync)
        {
            OutboundCommandRecord record = new(
                dispatch.ActionId,
                _sessionId,
                origin,
                text,
                envelope.Timestamp,
                _lastSourceSequence,
                OutboundCommandState.Dispatched,
                dispatch.Provenance?.ParentOperationId);

            _records.Add(record);
            if (_records.Count > _capacity)
            {
                _records.RemoveRange(0, _records.Count - _capacity);
            }
        }
    }

    private void UpdateState(Guid commandId, OutboundCommandState state)
    {
        lock (_sync)
        {
            int index = _records.FindLastIndex(record => record.CommandId == commandId);
            if (index >= 0)
            {
                _records[index] = _records[index] with { State = state };
            }
        }
    }

    private void MarkQueueCleared(long sourceSequence)
    {
        lock (_sync)
        {
            _lastSourceSequence = sourceSequence;
            int clearIndex = _records.FindLastIndex(record =>
                record.SessionId == _sessionId &&
                record.Text.Equals("clear", StringComparison.OrdinalIgnoreCase));
            if (clearIndex < 0)
            {
                return;
            }

            // The server confirms its queue was cleared, but does not report which
            // individual queued commands had already executed. Preserve the clear
            // command itself and only mark later unresolved commands conservatively.
            for (int index = clearIndex + 1; index < _records.Count; index++)
            {
                OutboundCommandRecord record = _records[index];
                if (record.SessionId != _sessionId ||
                    record.State is OutboundCommandState.ServerQueueCleared or OutboundCommandState.SessionEnded)
                {
                    continue;
                }
                _records[index] = record with { State = OutboundCommandState.ServerQueueCleared };
            }
        }
    }

    private void BeginSession(SessionId sessionId)
    {
        lock (_sync)
        {
            _records.Clear();
            _sessionId = sessionId;
            _lastSourceSequence = null;
        }
    }

    private void MarkSessionEnded()
    {
        lock (_sync)
        {
            for (int index = 0; index < _records.Count; index++)
            {
                if (_records[index].State != OutboundCommandState.SessionEnded)
                {
                    _records[index] = _records[index] with { State = OutboundCommandState.SessionEnded };
                }
            }
        }
    }
}
