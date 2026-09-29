using System.Text;
using System.Threading.Channels;
using JevMud.Contracts.Events;
using JevMud.Contracts.State;
using JevMud.Core.Events;
using JevMud.Transport.Text;

namespace JevMud.Adapters.Avendar;

public sealed class AvendarGameAdapter
{
    private enum ResponseMode
    {
        None,
        Score,
        Skills,
        Spells,
        Equipment,
        Inventory,
        ItemIdentification,
        AbilityHelp,
        Where
    }

    private readonly ChannelReader<EventEnvelope> _events;
    private readonly IEventSink _sink;
    private readonly AvendarPromptStreamProcessor _streamProcessor = new();
    private readonly AvendarSemanticParser _semanticParser = new();
    private readonly AvendarCommunicationParser _communicationParser = new();
    private readonly AvendarRoomContentsParser _roomContentsParser = new();
    private readonly AnsiStripper _ansiStripper = new();
    private readonly StringBuilder _lineBuffer = new();
    private readonly List<string> _responseLines = [];
    private readonly Queue<PendingNavigationResponse> _pendingNavigationResponses = new();
    private bool _navigationResponseSawText;
    private ResponseMode _responseMode;
    private bool _scoreCorePublished;
    private SessionInputMode _inputMode = SessionInputMode.Unknown;

    private sealed record PendingNavigationResponse(Guid ActionId, string Direction, bool ExplicitFailureObserved = false);

    public AvendarGameAdapter(ChannelReader<EventEnvelope> events, IEventSink sink)
    {
        _events = events;
        _sink = sink;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (EventEnvelope envelope in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    switch (envelope.Payload)
                    {
                        case TextReceived text:
                            await ProcessTextAsync(text.Text, cancellationToken).ConfigureAwait(false);
                            break;
                        case ActionDispatching action:
                            await BeginResponseCollectionAsync(action, cancellationToken).ConfigureAwait(false);
                            break;
                        case ProtocolStateChanged protocol when protocol.Protocol.Equals("ECHO", StringComparison.OrdinalIgnoreCase):
                            await HandleEchoProtocolAsync(protocol.Enabled, cancellationToken).ConfigureAwait(false);
                            break;
                        case ConnectionStateChanged connection when connection.Status != ConnectionStatus.Connected:
                            ResetSessionParsing();
                            break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ResetSessionParsing();
                    await _sink.PublishAsync(
                        new ComponentError("Avendar adapter", exception.Message),
                        "adapter.avendar",
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessTextAsync(string rawText, CancellationToken cancellationToken)
    {
        // Display is an immutable projection of the decoded server stream. Semantic
        // parsing may recognize or split frames, but it must never filter game output.
        await _sink.PublishAsync(new GameTextReceived(rawText), "adapter.avendar.display", cancellationToken)
            .ConfigureAwait(false);

        foreach (AvendarStreamToken token in _streamProcessor.Process(rawText))
        {
            switch (token)
            {
                case AvendarDisplayToken display:
                    await ProcessDisplayAsync(display.Text, cancellationToken).ConfigureAwait(false);
                    break;
                case AvendarPromptToken prompt:
                    await FlushPartialLineAsync(cancellationToken).ConfigureAwait(false);
                    // Avendar can emit a telemetry prompt immediately before a command response.
                    // Do not terminate a response capture until we have actually observed response text.
                    // This is particularly important for equipment/id/help, where the leading prompt
                    // otherwise caused a complete-but-empty snapshot and discarded the real response.
                    if (_responseMode != ResponseMode.None && _responseLines.Any(line => !string.IsNullOrWhiteSpace(line)))
                    {
                        await FinalizeResponseAsync(cancellationToken, complete: true).ConfigureAwait(false);
                    }
                    if (AvendarPromptParser.TryParse(prompt.Text, out CharacterPromptObserved? observed) &&
                        observed is not null)
                    {
                        await _sink.PublishAsync(observed, "adapter.avendar.prompt", cancellationToken)
                            .ConfigureAwait(false);
                        await PublishInputModeAsync(SessionInputMode.Normal, cancellationToken).ConfigureAwait(false);

                        RoomObservationObserved? completedRoom = null;
                        if (_roomContentsParser.TryComplete(observed.RoomName, out RoomObservationObserved? room) &&
                            room is not null)
                        {
                            completedRoom = room;
                            await _sink.PublishAsync(room, "adapter.avendar.room", cancellationToken)
                                .ConfigureAwait(false);
                        }

                        await CompleteNavigationResponseAsync(completedRoom is not null, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _roomContentsParser.Reset();
                        await _sink.PublishAsync(
                            new ComponentError("Avendar prompt parser", "Received malformed Jev telemetry prompt."),
                            "adapter.avendar.prompt",
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;
            }
        }
    }

    private async Task ProcessDisplayAsync(string rawText, CancellationToken cancellationToken)
    {
        if (rawText.Length == 0)
        {
            return;
        }

        string plainText = _ansiStripper.Process(Encoding.UTF8.GetBytes(rawText));
        if (_pendingNavigationResponses.Count > 0 && !string.IsNullOrWhiteSpace(plainText))
        {
            _navigationResponseSawText = true;
        }
        foreach (char value in plainText)
        {
            if (value == '\r')
            {
                continue;
            }

            if (value == '\n')
            {
                await ProcessLineAsync(_lineBuffer.ToString(), cancellationToken).ConfigureAwait(false);
                _lineBuffer.Clear();
            }
            else
            {
                _lineBuffer.Append(value);
            }
        }

        await ObservePartialInputPromptAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushPartialLineAsync(CancellationToken cancellationToken)
    {
        if (_lineBuffer.Length == 0)
        {
            return;
        }

        await ProcessLineAsync(_lineBuffer.ToString(), cancellationToken).ConfigureAwait(false);
        _lineBuffer.Clear();
    }

    private async Task ProcessLineAsync(string line, CancellationToken cancellationToken)
    {
        if (_responseMode != ResponseMode.None)
        {
            _responseLines.Add(line);
            if (_responseMode == ResponseMode.Score && !_scoreCorePublished)
            {
                await TryPublishCompleteScoreCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        IReadOnlyList<IMudEvent> semanticEvents = _semanticParser.ParseLine(line);
        foreach (IMudEvent mudEvent in semanticEvents)
        {
            if (mudEvent is NavigationFailed)
            {
                MarkCurrentNavigationFailure();
            }

            await _sink.PublishAsync(mudEvent, "adapter.avendar.semantic", cancellationToken)
                .ConfigureAwait(false);
        }

        bool communicationClaimed = _communicationParser.TryParse(line, out CommunicationObserved? communication);
        if (communication is not null)
        {
            await _sink.PublishAsync(communication, "adapter.avendar.communication", cancellationToken)
                .ConfigureAwait(false);
        }

        SessionInputModeChanged? session = AvendarSessionParser.ParseLine(line);
        if (session is not null)
        {
            await PublishInputModeAsync(session.Mode, cancellationToken).ConfigureAwait(false);
        }

        _roomContentsParser.ObserveLine(
            line,
            claimedByAnotherParser: _responseMode != ResponseMode.None ||
                                    semanticEvents.Count > 0 || communicationClaimed || session is not null);
    }

    private async Task ObservePartialInputPromptAsync(CancellationToken cancellationToken)
    {
        if (_lineBuffer.Length == 0) return;

        SessionInputModeChanged? session = AvendarSessionParser.ParseLine(_lineBuffer.ToString());
        if (session is not null)
        {
            await PublishInputModeAsync(session.Mode, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleEchoProtocolAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled)
        {
            if (_inputMode is SessionInputMode.LoginName or SessionInputMode.LoginPassword)
            {
                await PublishInputModeAsync(SessionInputMode.LoginPassword, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (_inputMode == SessionInputMode.LoginPassword)
        {
            await PublishInputModeAsync(SessionInputMode.Normal, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishInputModeAsync(SessionInputMode mode, CancellationToken cancellationToken)
    {
        if (_inputMode == mode) return;

        _inputMode = mode;
        await _sink.PublishAsync(
            new SessionInputModeChanged(mode),
            "adapter.avendar.session",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task TryPublishCompleteScoreCoreAsync(CancellationToken cancellationToken)
    {
        if (!AvendarScoreParser.TryParse(_responseLines, out CharacterScoreObserved? score) ||
            score is null || !IsCompleteScoreCore(score))
        {
            return;
        }

        _scoreCorePublished = true;
        await _sink.PublishAsync(score, "adapter.avendar.score", cancellationToken).ConfigureAwait(false);
    }

    private static bool IsCompleteScoreCore(CharacterScoreObserved score)
    {
        string[] requiredAttributes = ["Str", "Int", "Wis", "Dex", "Con", "Chr"];
        CharacterProfileState profile = score.Profile;
        CharacterCombatStats combat = score.CombatStats;
        InventorySummary inventory = score.Inventory;

        return !string.IsNullOrWhiteSpace(profile.Name) &&
               !string.IsNullOrWhiteSpace(profile.Lineage) &&
               !string.IsNullOrWhiteSpace(profile.ClassName) &&
               profile.Level is not null &&
               !string.IsNullOrWhiteSpace(profile.Gender) &&
               profile.Age is not null &&
               !string.IsNullOrWhiteSpace(profile.Alignment) &&
               requiredAttributes.All(score.Attributes.ContainsKey) &&
               score.HitPoints.Current is not null && score.HitPoints.Maximum is not null &&
               score.Mana.Current is not null && score.Mana.Maximum is not null &&
               score.Movement.Current is not null && score.Movement.Maximum is not null &&
               score.Experience is not null && score.ExperienceToLevel is not null && score.Exploration is not null &&
               combat.Hitroll is not null && combat.Damroll is not null && combat.Saves is not null &&
               combat.ArmorClass is not null && !string.IsNullOrWhiteSpace(combat.ArmorClassDescriptor) &&
               inventory.Items is not null && inventory.MaxItems is not null &&
               inventory.Weight is not null && inventory.MaxWeight is not null &&
               (inventory.Copper is not null || inventory.Silver is not null || inventory.Gold is not null);
    }

    private async Task BeginResponseCollectionAsync(ActionDispatching action, CancellationToken cancellationToken)
    {
        if (action.Sensitive)
        {
            return;
        }

        string normalized = action.Command.Trim();

        if (TryNormalizeMovement(normalized, out string direction))
        {
            _pendingNavigationResponses.Enqueue(new PendingNavigationResponse(action.ActionId, direction));
            await _sink.PublishAsync(
                new NavigationAttempted(direction, action.ActionId),
                "adapter.avendar.navigation",
                cancellationToken).ConfigureAwait(false);
        }

        if (AvendarSessionParser.IsEditorCommand(normalized))
        {
            await _sink.PublishAsync(
                new SessionInputModeChanged(JevMud.Contracts.State.SessionInputMode.Editor),
                "adapter.avendar.session",
                cancellationToken).ConfigureAwait(false);
        }

        // A blank command is Avendar's pager continuation. Keep collecting paged ability responses.
        if (normalized.Length == 0 && _responseMode is ResponseMode.Skills or ResponseMode.Spells or ResponseMode.AbilityHelp)
        {
            return;
        }

        if (_responseMode != ResponseMode.None)
        {
            await FinalizeResponseAsync(cancellationToken, complete: false).ConfigureAwait(false);
        }

        if (normalized.Equals("score", StringComparison.OrdinalIgnoreCase))
        {
            _responseMode = ResponseMode.Score;
        }
        else if (normalized.Equals("skills", StringComparison.OrdinalIgnoreCase))
        {
            _responseMode = ResponseMode.Skills;
        }
        else if (normalized.Equals("spells", StringComparison.OrdinalIgnoreCase))
        {
            _responseMode = ResponseMode.Spells;
        }
        else if (normalized.Equals("equipment", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("eq", StringComparison.OrdinalIgnoreCase))
        {
            _responseMode = ResponseMode.Equipment;
        }
        else if (normalized.Equals("inventory", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("inv", StringComparison.OrdinalIgnoreCase))
        {
            _responseMode = ResponseMode.Inventory;
        }
        else if (IsCommand(normalized, "id"))
        {
            _responseMode = ResponseMode.ItemIdentification;
        }
        else if (IsCommand(normalized, "help"))
        {
            _responseMode = ResponseMode.AbilityHelp;
        }
        else if (normalized.Equals("where", StringComparison.OrdinalIgnoreCase))
        {
            _responseMode = ResponseMode.Where;
        }

        _responseLines.Clear();
        _scoreCorePublished = false;
        if (_responseMode != ResponseMode.None)
        {
            await _sink.PublishAsync(
                new ResponseCaptureChanged(ToCaptureKind(_responseMode)),
                "adapter.avendar.response",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FinalizeResponseAsync(CancellationToken cancellationToken, bool complete)
    {
        if (_responseMode == ResponseMode.None)
        {
            return;
        }

        try
        {
            switch (_responseMode)
            {
                case ResponseMode.Score:
                    if (AvendarScoreParser.TryParse(_responseLines, out CharacterScoreObserved? score) &&
                        score is not null &&
                        (!_scoreCorePublished || score.Effects is not null || score.Conditions is { Count: > 0 }))
                    {
                        await _sink.PublishAsync(score, "adapter.avendar.score", cancellationToken)
                            .ConfigureAwait(false);
                    }
                    break;
                case ResponseMode.Skills:
                    if (AvendarSkillsParser.TryParse(_responseLines, out SkillsSnapshotObserved? skills) &&
                        skills is not null)
                    {
                        SkillsSnapshotObserved snapshot = skills with
                        {
                            Completeness = complete ? ObservationCompleteness.Complete : ObservationCompleteness.Partial
                        };
                        await _sink.PublishAsync(snapshot, "adapter.avendar.skills", cancellationToken)
                            .ConfigureAwait(false);
                    }
                    break;
                case ResponseMode.Spells:
                    if (AvendarSpellsParser.TryParse(_responseLines, out SpellsSnapshotObserved? spells) &&
                        spells is not null)
                    {
                        SpellsSnapshotObserved snapshot = spells with
                        {
                            Completeness = complete ? ObservationCompleteness.Complete : ObservationCompleteness.Partial
                        };
                        await _sink.PublishAsync(snapshot, "adapter.avendar.spells", cancellationToken)
                            .ConfigureAwait(false);
                    }
                    break;
                case ResponseMode.Equipment:
                    if (AvendarEquipmentParser.TryParse(_responseLines, out IReadOnlyList<EquipmentSlotState>? equipment) &&
                        equipment is not null)
                    {
                        await _sink.PublishAsync(
                            new EquipmentSnapshotObserved(
                                equipment,
                                complete ? ObservationCompleteness.Complete : ObservationCompleteness.Partial),
                            "adapter.avendar.equipment",
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case ResponseMode.Inventory:
                    if (AvendarInventoryParser.TryParse(_responseLines, out IReadOnlyList<string>? inventory) &&
                        inventory is not null)
                    {
                        await _sink.PublishAsync(
                            new InventorySnapshotObserved(
                                inventory,
                                complete ? ObservationCompleteness.Complete : ObservationCompleteness.Partial),
                            "adapter.avendar.inventory",
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case ResponseMode.ItemIdentification:
                    if (AvendarItemIdentificationParser.TryParse(_responseLines, out ItemIdentified? item) &&
                        item is not null)
                    {
                        await _sink.PublishAsync(item, "adapter.avendar.item-id", cancellationToken)
                            .ConfigureAwait(false);
                    }
                    break;
                case ResponseMode.AbilityHelp:
                    if (AvendarAbilityHelpParser.TryParse(_responseLines, out AbilityHelpObserved? help) &&
                        help is not null)
                    {
                        await _sink.PublishAsync(help, "adapter.avendar.help", cancellationToken)
                            .ConfigureAwait(false);
                    }
                    break;
                case ResponseMode.Where:
                    if (TryParseWhereArea(_responseLines, out string? area) && !string.IsNullOrWhiteSpace(area))
                    {
                        await _sink.PublishAsync(new AreaObserved(area), "adapter.avendar.where", cancellationToken)
                            .ConfigureAwait(false);
                    }
                    break;
            }
        }
        finally
        {
            _responseMode = ResponseMode.None;
            _responseLines.Clear();
            _scoreCorePublished = false;
            await _sink.PublishAsync(
                new ResponseCaptureChanged(ResponseCaptureKind.None),
                "adapter.avendar.response",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static ResponseCaptureKind ToCaptureKind(ResponseMode mode) => mode switch
    {
        ResponseMode.Score => ResponseCaptureKind.Score,
        ResponseMode.Skills => ResponseCaptureKind.Skills,
        ResponseMode.Spells => ResponseCaptureKind.Spells,
        ResponseMode.Equipment => ResponseCaptureKind.Equipment,
        ResponseMode.Inventory => ResponseCaptureKind.Inventory,
        ResponseMode.ItemIdentification => ResponseCaptureKind.ItemIdentification,
        ResponseMode.AbilityHelp => ResponseCaptureKind.AbilityHelp,
        ResponseMode.Where => ResponseCaptureKind.Where,
        _ => ResponseCaptureKind.None
    };


    private static bool TryParseWhereArea(IEnumerable<string> lines, out string? area)
    {
        area = null;
        const string marker = "(currently in ";
        foreach (string line in lines)
        {
            int start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            start += marker.Length;
            int end = line.IndexOf("):", start, StringComparison.Ordinal);
            if (end < 0) end = line.IndexOf(')', start);
            if (end <= start) continue;
            string candidate = line[start..end].Trim();
            if (candidate.Length == 0) continue;
            area = candidate;
            return true;
        }
        return false;
    }

    private static bool IsCommand(string normalized, string command) =>
        normalized.Equals(command, StringComparison.OrdinalIgnoreCase) ||
        normalized.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeMovement(string command, out string direction)
    {
        direction = command.ToLowerInvariant() switch
        {
            "n" or "north" => "north",
            "e" or "east" => "east",
            "s" or "south" => "south",
            "w" or "west" => "west",
            "u" or "up" => "up",
            "d" or "down" => "down",
            _ => string.Empty
        };

        return direction.Length > 0;
    }

    private void MarkCurrentNavigationFailure()
    {
        if (_pendingNavigationResponses.Count == 0) return;
        PendingNavigationResponse[] pending = _pendingNavigationResponses.ToArray();
        _pendingNavigationResponses.Clear();
        for (int index = 0; index < pending.Length; index++)
        {
            _pendingNavigationResponses.Enqueue(index == 0
                ? pending[index] with { ExplicitFailureObserved = true }
                : pending[index]);
        }
    }

    private async Task CompleteNavigationResponseAsync(bool roomObserved, CancellationToken cancellationToken)
    {
        if (_pendingNavigationResponses.Count == 0 || !_navigationResponseSawText) return;

        PendingNavigationResponse pending = _pendingNavigationResponses.Dequeue();
        _navigationResponseSawText = false;
        if (pending.ExplicitFailureObserved) return;

        await _sink.PublishAsync(
            new NavigationResponseCompleted(pending.ActionId, pending.Direction, roomObserved),
            "adapter.avendar.navigation",
            cancellationToken).ConfigureAwait(false);
    }

    private void ResetSessionParsing()
    {
        _streamProcessor.Reset();
        _roomContentsParser.Reset();
        _ansiStripper.Reset();
        _lineBuffer.Clear();
        _responseLines.Clear();
        _pendingNavigationResponses.Clear();
        _navigationResponseSawText = false;
        _responseMode = ResponseMode.None;
        _scoreCorePublished = false;
        _inputMode = SessionInputMode.Unknown;
    }
}
