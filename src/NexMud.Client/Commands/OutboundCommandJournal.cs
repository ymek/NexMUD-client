using System.Collections.ObjectModel;
using System.Threading.Channels;
using NexMud.Contracts.Actions;
using NexMud.Contracts.Events;

namespace NexMud.Client.Commands;

/// <summary>
/// Bounded evidence journal for commands sent to the MUD. It intentionally does not
/// infer command completion from prompts or ordinary server output.
/// </summary>
public sealed class OutboundCommandJournal
{
    private readonly ChannelReader<EventEnvelope> _events;
    private readonly object _sync = new();
    private readonly List<OutboundCommandRecord> _records = [];
    private readonly int _capacity;

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
                        BeginSession();
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

    private void RecordDispatch(EventEnvelope envelope, ActionDispatching dispatch)
    {
        CommandOrigin origin = dispatch.Provenance?.Origin ?? CommandOrigin.User;
        string text = dispatch.Sensitive ? "<redacted>" : dispatch.Command;
        OutboundCommandRecord record = new(
            dispatch.ActionId,
            origin,
            text,
            envelope.Sequence,
            envelope.Timestamp,
            OutboundCommandState.Dispatched,
            dispatch.Provenance?.ParentOperationId);

        lock (_sync)
        {
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
            for (int index = 0; index < _records.Count; index++)
            {
                OutboundCommandRecord record = _records[index];
                if (record.State == OutboundCommandState.SessionEnded)
                {
                    continue;
                }

                // SourceSequence is the game-observation sequence rather than the global
                // event sequence, so it is evidence only. Do not try to identify which
                // commands executed before the server cleared its queue.
                _records[index] = record with { State = OutboundCommandState.ServerQueueCleared };
            }
        }
    }

    private void BeginSession()
    {
        lock (_sync)
        {
            _records.Clear();
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
