using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using NexMud.Contracts.Events;
using NexMud.Contracts.Gameplay;
using NexMud.Contracts.State;
using NexMud.Core.Events;
using NexMud.Transport.Text;

namespace NexMud.Adapters.Avendar;

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
        Where,
        Group,
        Effects,
        Scan
    }

    private readonly ChannelReader<EventEnvelope> _events;
    private readonly IEventSink _sink;
    private readonly AvendarPromptStreamProcessor _streamProcessor = new();
    private readonly AvendarSemanticParser _semanticParser = new();
    private readonly AvendarCommunicationParser _communicationParser = new();
    private readonly AvendarRoomContentsParser _roomContentsParser = new();
    private readonly AvendarObservationFactory _observationFactory = new();
    private readonly AnsiStripper _ansiStripper = new();
    private readonly StringBuilder _lineBuffer = new();
    private readonly List<string> _responseLines = [];
    private readonly Queue<PendingNavigationResponse> _pendingNavigationResponses = new();
    private bool _navigationResponseSawText;
    private bool _navigationSawOpaqueRoom;
    private long _currentSourceSequence;
    private string _currentObservationSessionId = string.Empty;
    private long _responseSourceSequence;
    private MovementCause? _pendingSpecialMovementCause;
    private long _pendingSpecialMovementSequence;
    private string? _lastObservedRoomId;
    private ResponseMode _responseMode;
    private bool _scoreCorePublished;
    private SessionInputMode _inputMode = SessionInputMode.Unknown;

    private sealed record PendingNavigationResponse(
        Guid ActionId,
        string Direction,
        MovementCause Cause,
        bool ExplicitFailureObserved = false);

    public AvendarGameAdapter(ChannelReader<EventEnvelope> events, IEventSink sink)
    {
        _events = events;
        _sink = sink;
    }

    public async ValueTask ReplayObservationAsync(
        GameObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        await _sink.PublishAsync(
            new GameObservationReceived(observation),
            "adapter.avendar.replay",
            cancellationToken).ConfigureAwait(false);
        await ProcessObservationAsync(observation, cancellationToken).ConfigureAwait(false);
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
                            GameObservation observation = _observationFactory.Create(
                                text.Text,
                                envelope.Timestamp);
                            await _sink.PublishAsync(
                                new GameObservationReceived(observation),
                                "adapter.avendar.observation",
                                cancellationToken).ConfigureAwait(false);
                            await ProcessObservationAsync(observation, cancellationToken).ConfigureAwait(false);
                            break;
                        case ActionDispatching action:
                            await PublishLocalCommandObservationAsync(action, envelope.Timestamp, cancellationToken)
                                .ConfigureAwait(false);
                            await BeginResponseCollectionAsync(action, cancellationToken).ConfigureAwait(false);
                            break;
                        case ProtocolStateChanged protocol:
                            await PublishProtocolObservationAsync(protocol, envelope.Timestamp, cancellationToken)
                                .ConfigureAwait(false);
                            if (protocol.Protocol.Equals("ECHO", StringComparison.OrdinalIgnoreCase))
                            {
                                await HandleEchoProtocolAsync(protocol.Enabled, cancellationToken).ConfigureAwait(false);
                            }
                            break;
                        case ConnectionStateChanged connection:
                            await PublishConnectionObservationAsync(connection, envelope.Timestamp, cancellationToken)
                                .ConfigureAwait(false);
                            if (connection.Status != ConnectionStatus.Connected)
                            {
                                ResetSessionParsing();
                            }
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

    private async Task PublishLocalCommandObservationAsync(
        ActionDispatching action,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        string text = action.Sensitive ? "<redacted>" : action.Command;
        GameObservation observation = _observationFactory.CreateEvidence(
            text,
            timestamp,
            ObservationKind.LocalCommandEcho,
            new ObservationMetadata(IsLocal: true));
        await _sink.PublishAsync(
            new GameObservationReceived(observation),
            "adapter.avendar.observation",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishProtocolObservationAsync(
        ProtocolStateChanged protocol,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        string text = protocol.Detail ?? $"{protocol.Protocol}:{(protocol.Enabled ? "enabled" : "disabled")}";
        IReadOnlyDictionary<string, string> fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enabled"] = protocol.Enabled.ToString()
        };
        GameObservation observation = _observationFactory.CreateEvidence(
            text,
            timestamp,
            ObservationKind.ProtocolEvent,
            new ObservationMetadata(Protocol: protocol.Protocol, Fields: fields));
        await _sink.PublishAsync(
            new GameObservationReceived(observation),
            "adapter.avendar.observation",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishConnectionObservationAsync(
        ConnectionStateChanged connection,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        string text = connection.Reason ?? connection.Status.ToString();
        IReadOnlyDictionary<string, string> fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = connection.Status.ToString(),
            ["host"] = connection.Host ?? string.Empty,
            ["port"] = connection.Port?.ToString() ?? string.Empty
        };
        GameObservation observation = _observationFactory.CreateEvidence(
            text,
            timestamp,
            ObservationKind.ConnectionEvent,
            new ObservationMetadata(Fields: fields));
        await _sink.PublishAsync(
            new GameObservationReceived(observation),
            "adapter.avendar.observation",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessObservationAsync(GameObservation observation, CancellationToken cancellationToken)
    {
        _currentSourceSequence = observation.Sequence;
        _currentObservationSessionId = observation.SessionId;
        string rawText = observation.RawText;

        // Display and semantics branch from the same immutable source observation.
        // Presentation transforms can therefore never alter parser evidence.
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
                        observed = observed with { SourceSequence = _currentSourceSequence };
                        await _sink.PublishAsync(observed, "adapter.avendar.prompt", cancellationToken)
                            .ConfigureAwait(false);
                        await _sink.PublishAsync(
                            new CharacterPromptSnapshotObserved(
                                AvendarPromptSnapshotParser.FromTelemetry(observed, _currentSourceSequence)),
                            "adapter.avendar.prompt",
                            cancellationToken).ConfigureAwait(false);
                        await PublishInputModeAsync(SessionInputMode.Normal, cancellationToken).ConfigureAwait(false);

                        RoomObservationObserved? completedRoom = null;
                        if (_roomContentsParser.TryComplete(observed.RoomName, out RoomObservationObserved? room) &&
                            room is not null)
                        {
                            completedRoom = StampRoomObservation(room, _currentSourceSequence, _currentObservationSessionId);
                            await _sink.PublishAsync(completedRoom, "adapter.avendar.room", cancellationToken)
                                .ConfigureAwait(false);
                            await ReconcileUncorrelatedRoomTransitionAsync(completedRoom, cancellationToken)
                                .ConfigureAwait(false);
                            await CompleteSpecialMovementAsync(completedRoom, cancellationToken).ConfigureAwait(false);
                        }

                        await CompleteNavigationResponseAsync(completedRoom, cancellationToken)
                            .ConfigureAwait(false);
                        if (completedRoom is null)
                        {
                            _pendingSpecialMovementCause = null;
                            _pendingSpecialMovementSequence = 0;
                        }
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
        string semanticLine = line;
        if (AvendarPromptSnapshotParser.TryParseResourceLine(
                line,
                _currentSourceSequence,
                out CharacterPromptSnapshot? resourcePrompt) &&
            resourcePrompt is not null)
        {
            await CompleteLegacyPromptBoundaryAsync(cancellationToken).ConfigureAwait(false);
            await _sink.PublishAsync(
                new CharacterPromptSnapshotObserved(resourcePrompt),
                "adapter.avendar.prompt",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (AvendarPromptSnapshotParser.TryExtractGameClockPrefix(
                line,
                _currentSourceSequence,
                out CharacterPromptSnapshot? clockPrompt,
                out string remainder) &&
            clockPrompt is not null)
        {
            await CompleteLegacyPromptBoundaryAsync(cancellationToken).ConfigureAwait(false);
            await _sink.PublishAsync(
                new CharacterPromptSnapshotObserved(clockPrompt),
                "adapter.avendar.prompt",
                cancellationToken).ConfigureAwait(false);
            semanticLine = remainder;
            if (string.IsNullOrWhiteSpace(semanticLine))
            {
                return;
            }
        }

        await TryBeginInferredResponseCaptureAsync(semanticLine, cancellationToken)
            .ConfigureAwait(false);

        if (_pendingNavigationResponses.Count > 0 &&
            !string.IsNullOrWhiteSpace(semanticLine) &&
            !IsPendingNavigationEcho(semanticLine))
        {
            _navigationResponseSawText = true;
        }

        if (semanticLine.Trim().Equals("It is pitch black ...", StringComparison.OrdinalIgnoreCase))
        {
            if (_pendingNavigationResponses.Count > 0)
            {
                _navigationSawOpaqueRoom = true;
                _navigationResponseSawText = true;
                await CompleteNavigationResponseAsync(null, cancellationToken).ConfigureAwait(false);
            }

            if (_pendingSpecialMovementCause is not null)
            {
                MovementCause cause = _pendingSpecialMovementCause.Value;
                long sourceSequence = Math.Max(_pendingSpecialMovementSequence, _currentSourceSequence);
                _pendingSpecialMovementCause = null;
                _pendingSpecialMovementSequence = 0;
                await _sink.PublishAsync(
                    new MovementObserved(new MovementObservation(
                        cause,
                        MovementResult.SucceededUnknownRoom,
                        null,
                        null,
                        "It is pitch black ...",
                        sourceSequence)),
                    "adapter.avendar.navigation",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (_responseMode != ResponseMode.None)
        {
            _responseLines.Add(semanticLine);
            _responseSourceSequence = _currentSourceSequence;
            if (_responseMode == ResponseMode.Score && !_scoreCorePublished)
            {
                await TryPublishCompleteScoreCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        IReadOnlyList<IMudEvent> semanticEvents = _semanticParser.ParseLine(semanticLine, _currentSourceSequence);
        foreach (IMudEvent mudEvent in semanticEvents)
        {
            if (mudEvent is NavigationFailed ||
                mudEvent is MovementObserved
                { Movement.Result: MovementResult.Blocked or MovementResult.CombatRestricted })
            {
                MarkCurrentNavigationFailure();
            }

            if (mudEvent is MovementObserved movement &&
                (movement.Movement.Result is MovementResult.SucceededUnknownRoom or MovementResult.Unknown) &&
                movement.Movement.Cause is not MovementCause.ManualDirection and not MovementCause.MapperRoute)
            {
                _pendingSpecialMovementCause = movement.Movement.Cause;
                _pendingSpecialMovementSequence = movement.Movement.SourceSequence;
            }

            await _sink.PublishAsync(mudEvent, "adapter.avendar.semantic", cancellationToken)
                .ConfigureAwait(false);
        }

        bool communicationClaimed = _communicationParser.TryParse(semanticLine, out CommunicationObserved? communication);
        if (communication is not null)
        {
            await _sink.PublishAsync(communication, "adapter.avendar.communication", cancellationToken)
                .ConfigureAwait(false);
        }

        SessionInputModeChanged? session = AvendarSessionParser.ParseLine(semanticLine);
        if (session is not null)
        {
            await PublishInputModeAsync(session.Mode, cancellationToken).ConfigureAwait(false);
        }

        _roomContentsParser.ObserveLine(
            semanticLine,
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

    private async Task CompleteLegacyPromptBoundaryAsync(CancellationToken cancellationToken)
    {
        if (_responseMode != ResponseMode.None &&
            _responseLines.Any(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            await FinalizeResponseAsync(cancellationToken, complete: true).ConfigureAwait(false);
        }

        RoomObservationObserved? completedRoom = null;
        if (_roomContentsParser.TryCompleteLatest(out RoomObservationObserved? room) && room is not null)
        {
            completedRoom = StampRoomObservation(room, _currentSourceSequence, _currentObservationSessionId);
            await _sink.PublishAsync(completedRoom, "adapter.avendar.room", cancellationToken)
                .ConfigureAwait(false);
            await ReconcileUncorrelatedRoomTransitionAsync(completedRoom, cancellationToken)
                .ConfigureAwait(false);
            await CompleteSpecialMovementAsync(completedRoom, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _roomContentsParser.Reset();
        }

        await CompleteNavigationResponseAsync(completedRoom, cancellationToken).ConfigureAwait(false);
        if (completedRoom is null)
        {
            _pendingSpecialMovementCause = null;
            _pendingSpecialMovementSequence = 0;
        }
    }

    private async Task TryBeginInferredResponseCaptureAsync(
        string line,
        CancellationToken cancellationToken)
    {
        string trimmed = line.Trim();
        ResponseMode inferred = ResponseMode.None;
        if (trimmed.EndsWith("'s group:", StringComparison.OrdinalIgnoreCase))
        {
            inferred = ResponseMode.Group;
        }
        else if (trimmed.Equals("You are affected by the following:", StringComparison.OrdinalIgnoreCase))
        {
            inferred = ResponseMode.Effects;
        }
        else if (trimmed.StartsWith("You peer intently ", StringComparison.OrdinalIgnoreCase) &&
                 trimmed.EndsWith(".", StringComparison.Ordinal))
        {
            inferred = ResponseMode.Scan;
        }
        else if ((trimmed.StartsWith("You study ", StringComparison.OrdinalIgnoreCase) &&
                  trimmed.Contains(" carefully", StringComparison.OrdinalIgnoreCase)) ||
                 (trimmed.StartsWith("|", StringComparison.Ordinal) &&
                  trimmed.Contains("Object:", StringComparison.OrdinalIgnoreCase)))
        {
            inferred = ResponseMode.ItemIdentification;
        }

        if (inferred == ResponseMode.None || inferred == _responseMode)
        {
            return;
        }

        // Structural server evidence wins over command-response guesses. Expert play can
        // queue commands, so a later ActionDispatching event must not pin parsing to the
        // wrong response type when an unambiguous group/effect/scan/id frame arrives.
        if (_responseMode != ResponseMode.None &&
            _responseLines.Any(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            await FinalizeResponseAsync(cancellationToken, complete: false).ConfigureAwait(false);
        }

        _responseMode = inferred;
        _responseLines.Clear();
        _responseSourceSequence = _currentSourceSequence;
        _scoreCorePublished = false;
        await _sink.PublishAsync(
            new ResponseCaptureChanged(ToCaptureKind(inferred)),
            "adapter.avendar.response",
            cancellationToken).ConfigureAwait(false);
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
            MovementCause cause = action.Provenance?.Origin == NexMud.Contracts.Actions.CommandOrigin.Mapper
                ? MovementCause.MapperRoute
                : MovementCause.ManualDirection;
            _pendingNavigationResponses.Enqueue(new PendingNavigationResponse(action.ActionId, direction, cause));
            await _sink.PublishAsync(
                new NavigationAttempted(direction, action.ActionId),
                "adapter.avendar.navigation",
                cancellationToken).ConfigureAwait(false);
        }

        if (AvendarSessionParser.IsEditorCommand(normalized))
        {
            await _sink.PublishAsync(
                new SessionInputModeChanged(NexMud.Contracts.State.SessionInputMode.Editor),
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
        else if (normalized.Equals("gr", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("group", StringComparison.OrdinalIgnoreCase))
        {
            _responseMode = ResponseMode.Group;
        }
        else if (normalized.Equals("af", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("affects", StringComparison.OrdinalIgnoreCase))
        {
            _responseMode = ResponseMode.Effects;
        }
        else if (IsCommand(normalized, "scan") || IsCommand(normalized, "sca"))
        {
            _responseMode = ResponseMode.Scan;
        }

        _responseLines.Clear();
        _responseSourceSequence = 0;
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
                case ResponseMode.Group:
                    if (AvendarGroupParser.TryParse(_responseLines, _responseSourceSequence, out GroupSnapshot? group) &&
                        group is not null)
                    {
                        await _sink.PublishAsync(
                            new GroupSnapshotObserved(group),
                            "adapter.avendar.group",
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case ResponseMode.Effects:
                    if (AvendarEffectsParser.TryParse(_responseLines, _responseSourceSequence, out ActiveEffectsSnapshot? effects) &&
                        effects is not null)
                    {
                        await _sink.PublishAsync(
                            new ActiveEffectsSnapshotObserved(effects),
                            "adapter.avendar.effects",
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case ResponseMode.Scan:
                    if (AvendarScanParser.TryParse(_responseLines, _responseSourceSequence, out ScanObservation? scan) &&
                        scan is not null)
                    {
                        await _sink.PublishAsync(
                            new ScanUpdated(scan),
                            "adapter.avendar.scan",
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;
            }
        }
        finally
        {
            _responseMode = ResponseMode.None;
            _responseLines.Clear();
            _responseSourceSequence = 0;
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
        ResponseMode.Group => ResponseCaptureKind.Group,
        ResponseMode.Effects => ResponseCaptureKind.Effects,
        ResponseMode.Scan => ResponseCaptureKind.Scan,
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

    private bool IsPendingNavigationEcho(string line)
    {
        if (_pendingNavigationResponses.Count == 0 ||
            !TryNormalizeMovement(line.Trim(), out string echoedDirection))
        {
            return false;
        }

        return echoedDirection.Equals(
            _pendingNavigationResponses.Peek().Direction,
            StringComparison.OrdinalIgnoreCase);
    }

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

    private async Task CompleteNavigationResponseAsync(
        RoomObservationObserved? room,
        CancellationToken cancellationToken)
    {
        if (_pendingNavigationResponses.Count == 0 || !_navigationResponseSawText) return;

        PendingNavigationResponse pending = _pendingNavigationResponses.Dequeue();
        _navigationResponseSawText = false;
        bool opaque = _navigationSawOpaqueRoom;
        _navigationSawOpaqueRoom = false;
        if (pending.ExplicitFailureObserved) return;

        bool roomObserved = room is not null;
        await _sink.PublishAsync(
            new NavigationResponseCompleted(pending.ActionId, pending.Direction, roomObserved),
            "adapter.avendar.navigation",
            cancellationToken).ConfigureAwait(false);
        await _sink.PublishAsync(
            new MovementObserved(new MovementObservation(
                pending.Cause,
                roomObserved ? MovementResult.SucceededKnownRoom : MovementResult.SucceededUnknownRoom,
                pending.Direction,
                room?.RoomId,
                opaque ? "It is pitch black ..." : null,
                _currentSourceSequence,
                pending.ActionId)),
            "adapter.avendar.navigation",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileUncorrelatedRoomTransitionAsync(
        RoomObservationObserved room,
        CancellationToken cancellationToken)
    {
        bool changed = _lastObservedRoomId is not null &&
            !string.Equals(_lastObservedRoomId, room.RoomId, StringComparison.Ordinal);
        bool alreadyCorrelated = _pendingNavigationResponses.Count > 0 ||
            _pendingSpecialMovementCause is not null;

        _lastObservedRoomId = room.RoomId;
        if (!changed || alreadyCorrelated)
        {
            return;
        }

        // A room transition without a correlated direction/special-movement message is
        // still authoritative movement evidence. Preserve the unknown cause rather than
        // inventing teleport/follow semantics. This covers spoken teleports and other
        // game mechanics whose cause is not explicit in the observed output.
        await _sink.PublishAsync(
            new MovementObserved(new MovementObservation(
                MovementCause.Unknown,
                MovementResult.SucceededKnownRoom,
                null,
                room.RoomId,
                room.RoomName,
                room.SourceSequence)),
            "adapter.avendar.navigation",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteSpecialMovementAsync(
        RoomObservationObserved room,
        CancellationToken cancellationToken)
    {
        if (_pendingSpecialMovementCause is null) return;

        MovementCause cause = _pendingSpecialMovementCause.Value;
        long sourceSequence = Math.Max(_pendingSpecialMovementSequence, room.SourceSequence);
        _pendingSpecialMovementCause = null;
        _pendingSpecialMovementSequence = 0;
        await _sink.PublishAsync(
            new MovementObserved(new MovementObservation(
                cause,
                cause is MovementCause.Teleport or MovementCause.Summon
                    ? MovementResult.Teleported
                    : cause == MovementCause.Forced
                        ? MovementResult.Forced
                        : MovementResult.SucceededKnownRoom,
                null,
                room.RoomId,
                room.RoomName,
                sourceSequence)),
            "adapter.avendar.navigation",
            cancellationToken).ConfigureAwait(false);
    }

    private static RoomObservationObserved StampRoomObservation(
        RoomObservationObserved room,
        long sourceSequence,
        string sessionId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"room-observation|{sessionId}|{sourceSequence}|{room.RoomId}|{room.DescriptionFingerprint}"));
        return room with
        {
            ObservationId = new Guid(hash.AsSpan(0, 16)),
            SourceSequence = sourceSequence
        };
    }

    private void ResetSessionParsing()
    {
        _streamProcessor.Reset();
        _roomContentsParser.Reset();
        _observationFactory.Reset();
        _ansiStripper.Reset();
        _lineBuffer.Clear();
        _responseLines.Clear();
        _pendingNavigationResponses.Clear();
        _navigationResponseSawText = false;
        _navigationSawOpaqueRoom = false;
        _currentSourceSequence = 0;
        _currentObservationSessionId = string.Empty;
        _responseSourceSequence = 0;
        _pendingSpecialMovementCause = null;
        _pendingSpecialMovementSequence = 0;
        _lastObservedRoomId = null;
        _responseMode = ResponseMode.None;
        _scoreCorePublished = false;
        _inputMode = SessionInputMode.Unknown;
    }
}
