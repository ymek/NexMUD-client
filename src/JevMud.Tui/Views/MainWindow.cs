using System.Text;
using System.Threading.Channels;
using JevMud.Contracts.Events;
using JevMud.Contracts.Jev;
using JevMud.Contracts.State;
using JevMud.Core.Events;
using JevMud.Client.Commands;
using JevMud.Client.Interaction;
using JevMud.Client.Runtime;
using JevMud.Client.Settings;
using JevMud.Transport.Text;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace JevMud.Tui.Views;

public sealed class MainWindow : Runnable
{
    private const int MaxEventLines = 200;

    private readonly JevMudRuntime _runtime;
    private readonly ChannelReader<EventEnvelope> _events;
    private readonly ChannelReader<StateSnapshot> _snapshots;
    private readonly CancellationTokenSource _viewCts = new();
    private readonly Channel<string> _submittedCommands = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private readonly Queue<string> _eventLines = new();
    private readonly AnsiConsoleView _gameView;
    private readonly TextView _stateView;
    private readonly TextView _eventView;
    private readonly TextView _jevView;
    private readonly CommandInputView _input;
    private readonly Label _header;
    private StateSnapshot _lastSnapshot = StateSnapshot.Initial;
    private JevDecisionTrace? _lastDecision;
    private Task? _eventConsumer;
    private Task? _stateConsumer;
    private Task? _commandConsumer;
    private bool _pagerWaiting;

    public MainWindow(JevMudRuntime runtime)
    {
        _runtime = runtime;
        _events = runtime.Events.SubscribeLossless();
        _snapshots = runtime.State.Subscribe();
        _runtime.Interaction.World.Appended += HandleWorldBufferAppended;
        _runtime.Interaction.RuleDiagnostic += HandleOutputRuleDiagnostic;

        Title = "NexMUD";
        SetScheme(UiTheme.Panel);

        _header = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            CanFocus = false
        };
        _header.SetScheme(UiTheme.Status);

        FrameView gameFrame = CreateFrame(" Game ");
        gameFrame.X = 0;
        gameFrame.Y = 1;
        gameFrame.Width = Dim.Percent(65);
        gameFrame.Height = Dim.Percent(64);

        _gameView = new AnsiConsoleView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        gameFrame.Add(_gameView);

        FrameView eventsFrame = CreateFrame(" Events ");
        eventsFrame.X = 0;
        eventsFrame.Y = Pos.Bottom(gameFrame);
        eventsFrame.Width = Dim.Percent(65);
        eventsFrame.Height = Dim.Fill(1);

        _eventView = CreateReadOnlyTextView(wordWrap: false);
        eventsFrame.Add(_eventView);

        FrameView stateFrame = CreateFrame(" State ");
        stateFrame.X = Pos.Right(gameFrame);
        stateFrame.Y = 1;
        stateFrame.Width = Dim.Fill();
        stateFrame.Height = Dim.Percent(55);

        _stateView = CreateReadOnlyTextView(wordWrap: false);
        stateFrame.Add(_stateView);

        FrameView jevFrame = CreateFrame(" Jev ");
        jevFrame.X = Pos.Right(gameFrame);
        jevFrame.Y = Pos.Bottom(stateFrame);
        jevFrame.Width = Dim.Fill();
        jevFrame.Height = Dim.Fill(1);

        _jevView = CreateReadOnlyTextView(wordWrap: false);
        jevFrame.Add(_jevView);

        Label prompt = new()
        {
            Text = "> ",
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = 2,
            Height = 1,
            CanFocus = false
        };
        prompt.SetScheme(UiTheme.Input);

        _input = new CommandInputView
        {
            X = 2,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            CanFocus = true,
            HistoryPrevious = current => _runtime.Interaction.History.Previous(current),
            HistoryNext = () => _runtime.Interaction.History.Next()
        };
        _input.SetScheme(UiTheme.Input);
        _input.Accepted += (_, _) => SubmitInput();

        AddCommand(Command.PageUp, () =>
        {
            _gameView.ScrollPage(-1);
            RefreshHeader();
            return true;
        });
        AddCommand(Command.PageDown, () =>
        {
            _gameView.ScrollPage(1);
            RefreshHeader();
            return true;
        });
        AddCommand(Command.End, () =>
        {
            _gameView.ScrollToBottom();
            RefreshHeader();
            return true;
        });

        KeyBindings.Add(Key.PageUp, Command.PageUp);
        KeyBindings.Add(Key.PageDown, Command.PageDown);
        KeyBindings.Add(Key.G.WithCtrl, Command.End);

        Add(_header, gameFrame, eventsFrame, stateFrame, jevFrame, prompt, _input);
        Initialized += (_, _) => StartConsumers();
    }

    private static FrameView CreateFrame(string title)
    {
        FrameView frame = new()
        {
            Title = title,
            CanFocus = false
        };
        frame.SetScheme(UiTheme.Panel);
        return frame;
    }

    private static TextView CreateReadOnlyTextView(bool wordWrap)
    {
        TextView textView = new()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = wordWrap,
            CanFocus = false
        };
        textView.SetScheme(UiTheme.Panel);
        return textView;
    }

    private void StartConsumers()
    {
        if (_eventConsumer is not null || _stateConsumer is not null)
        {
            return;
        }

        _eventConsumer = Task.Run(() => ConsumeEventsAsync(_viewCts.Token), CancellationToken.None);
        _stateConsumer = Task.Run(() => ConsumeStateAsync(_viewCts.Token), CancellationToken.None);
        _commandConsumer = Task.Run(() => ConsumeCommandsAsync(_viewCts.Token), CancellationToken.None);
        RenderState(_runtime.State.Current);
        RenderAuthority(_runtime.Authority.Current, null);
        RefreshHeader();
        _input.SetFocus();
    }

    private void SubmitInput()
    {
        string input = _input.Text?.ToString() ?? string.Empty;
        _input.Text = string.Empty;
        RefreshHeader();

        if (!_submittedCommands.Writer.TryWrite(input))
            AppendSystemMessage("Command queue is unavailable.");
    }

    private async Task ConsumeCommandsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (string input in _submittedCommands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    InputSubmissionResult submission = await _runtime.Interaction.Input.SubmitAsync(
                        new InputRequest(InputSourceKind.Keyboard, input, DateTimeOffset.Now, Guid.NewGuid()),
                        _lastSnapshot.Session.InputMode,
                        cancellationToken).ConfigureAwait(false);
                    foreach (LocalCommandResult result in submission.Results)
                    {
                        if (!string.IsNullOrWhiteSpace(result.Message))
                            AppendSystemMessage(result.Message);

                        if (result.ExitRequested)
                        {
                            IApplication? app = App;
                            if (app is not null) app.Invoke(app.RequestStop);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    AppendSystemMessage($"Command failed: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ConsumeEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (EventEnvelope envelope in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (envelope.Payload)
                {
                    case JevDecisionProduced decision:
                        _lastDecision = decision.Decision;
                        App?.Invoke(() => RenderDecision(decision.Decision));
                        break;
                    case JevAuthorityProfileChanged authority:
                        App?.Invoke(() => RenderAuthority(authority.Snapshot, _lastDecision));
                        break;
                    case ActionRejected rejected:
                        AppendSystemMessage($"Action rejected: {rejected.Reason}");
                        break;
                    case PagerPromptObserved:
                        _pagerWaiting = true;
                        App?.Invoke(() => RefreshHeader());
                        break;
                    case CharacterPromptObserved:
                        _pagerWaiting = false;
                        App?.Invoke(() => RefreshHeader());
                        break;
                    case ComponentError error:
                        AppendSystemMessage($"{error.Component}: {error.Message}");
                        break;
                }

                if (ShouldDisplayEvent(envelope.Payload))
                {
                    AppendEventLine(FormatEvent(envelope));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ConsumeStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (StateSnapshot snapshot in _snapshots.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _lastSnapshot = snapshot;
                App?.Invoke(() =>
                {
                    RenderState(snapshot);
                    RefreshHeader();
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void HandleOutputRuleDiagnostic(OutputRuleDiagnostic diagnostic) =>
        AppendSystemMessage($"Output rule {diagnostic.RuleId}: {diagnostic.Message}");

    private void HandleWorldBufferAppended(WorldBufferEntry entry)
    {
        if (entry.IsGagged) return;
        App?.Invoke(() =>
        {
            List<AnsiTextSegment> segments = [];
            string timestamp = FormatTimestamp(entry.Timestamp);
            if (timestamp.Length > 0)
                segments.Add(new AnsiTextSegment(timestamp, AnsiTextStyle.Default with { Faint = true }));
            foreach (WorldStyledRun run in entry.StyledRuns)
                segments.Add(new AnsiTextSegment(run.Text, ApplyOverride(run.AnsiStyle, run.Override)));
            _gameView.AppendSegments(segments);
            RefreshHeader();
        });
    }

    private string FormatTimestamp(DateTimeOffset timestamp)
    {
        DateTimeOffset local = timestamp.ToLocalTime();
        return (_runtime.Settings.Output ?? new OutputPreferences()).TimestampMode switch
        {
            TimestampRenderMode.Time => $"[{local:HH:mm:ss}] ",
            TimestampRenderMode.TimeWithMilliseconds => $"[{local:HH:mm:ss.fff}] ",
            TimestampRenderMode.DateTime => $"[{local:yyyy-MM-dd HH:mm:ss}] ",
            _ => string.Empty
        };
    }

    private static AnsiTextStyle ApplyOverride(AnsiTextStyle style, OutputPresentationStyle? overlay)
    {
        if (overlay is null) return style;
        return style with
        {
            Foreground = ParseColor(overlay.Foreground) ?? style.Foreground,
            Background = ParseColor(overlay.Background) ?? style.Background,
            Bold = style.Bold || overlay.Bold,
            Italic = style.Italic || overlay.Italic,
            Underline = style.Underline || overlay.Underline
        };
    }

    private static AnsiColor? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 7 || value[0] != '#') return null;
        return byte.TryParse(value.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out byte red) &&
               byte.TryParse(value.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out byte green) &&
               byte.TryParse(value.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out byte blue)
            ? new AnsiColor(red, green, blue)
            : null;
    }

    private void AppendEventLine(string line)
    {
        App?.Invoke(() =>
        {
            _eventLines.Enqueue(line);
            while (_eventLines.Count > MaxEventLines)
            {
                _eventLines.Dequeue();
            }

            _eventView.Text = string.Join(
                Environment.NewLine,
                _eventLines.Reverse().Take(80));
        });
    }

    private void AppendSystemMessage(string message)
    {
        string cleaned = message.Trim();
        AppendEventLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Client  {cleaned}");
    }

    private void RenderState(StateSnapshot snapshot)
    {
        string effects = snapshot.Character.Effects.Count == 0
            ? "none"
            : string.Join(", ", snapshot.Character.Effects);
        string conditions = snapshot.Character.Conditions.Count == 0
            ? "none"
            : string.Join(", ", snapshot.Character.Conditions);

        string exits = FormatExits(snapshot.Room.Exits);
        string profile = snapshot.Character.Profile.Name is null
            ? "-"
            : $"{snapshot.Character.Profile.Name}  L{snapshot.Character.Profile.Level?.ToString() ?? "?"} {snapshot.Character.Profile.ClassName ?? ""}".Trim();

        string lastPlayerDamage = FormatDamage(snapshot.Combat.LastPlayerDamage);
        string lastOpponentDamage = FormatDamage(snapshot.Combat.LastOpponentDamage);
        int availableSkills = snapshot.Character.Skills.Count(skill => skill.Availability == SkillAvailability.Available);
        int staleSkills = snapshot.Character.Skills.Count(skill => !skill.IsFresh);
        int availableSpells = snapshot.Character.Spells.Count(spell => spell.Availability == SkillAvailability.Available);
        int staleSpells = snapshot.Character.Spells.Count(spell => !spell.IsFresh);
        string occupants = FormatRoomContents(snapshot.Room.Occupants, 5);
        string objects = FormatRoomContents(snapshot.Room.Objects, 3);
        string interactables = FormatRoomContents(snapshot.Room.Interactables, 4);
        string corpses = FormatRoomContents(snapshot.Room.Corpses, 3);
        string unknownContents = FormatRoomContents(snapshot.Room.UnknownContents, 2);
        string mainHand = snapshot.Character.Equipment.MainHand ?? "-";
        string wealth = FormatWealth(snapshot.Character.Inventory);
        string roomId = snapshot.Room.Id is null ? "-" : snapshot.Room.Id.Replace("avendar:", string.Empty, StringComparison.Ordinal);

        _stateView.Text = $"""
 v{snapshot.Version}  {snapshot.Session.ConnectionStatus}  Input {snapshot.Session.InputMode}

 Character
   {profile}
   HP         {FormatPair(snapshot.Character.HitPoints)}
   Mana       {FormatPair(snapshot.Character.Mana)}
   Move       {FormatPair(snapshot.Character.Movement)}
   Position   {snapshot.Character.Position ?? "-"}
   XP         {snapshot.Character.Experience?.ToString() ?? "-"}
   To level   {snapshot.Character.ExperienceToLevel?.ToString() ?? "-"}
   Explore    {snapshot.Character.Exploration?.ToString() ?? "-"}
   Wealth     {wealth}
   Effects    {effects}
   Conditions {conditions}
   Skills     {availableSkills}/{snapshot.Character.Skills.Count} ({snapshot.Character.SkillsCompleteness}, {staleSkills} stale)
   Spells     {availableSpells}/{snapshot.Character.Spells.Count} ({snapshot.Character.SpellsCompleteness}, {staleSpells} stale)
   Wielded    {mainHand}

 Room
   {snapshot.Room.Name ?? "-"}
   Id         {roomId}
   Contents   {snapshot.Room.ContentsCompleteness}
   Exits      {exits}
   Terrain    {snapshot.Room.Terrain ?? "-"}
   Light      {snapshot.Room.Light ?? "-"}
   Occupants  {snapshot.Room.Occupants.Count}
 {occupants}
   Fixtures   {snapshot.Room.Interactables.Count}
 {interactables}
   Objects    {snapshot.Room.Objects.Count}
 {objects}
   Corpses    {snapshot.Room.Corpses.Count}
 {corpses}
   Unknown    {snapshot.Room.UnknownContents.Count}
 {unknownContents}
   Map        {snapshot.World.Rooms.Count} rooms / {snapshot.World.Edges.Count} edges

 Combat
   Active     {snapshot.Combat.Active}
   Target     {snapshot.Combat.TargetName ?? snapshot.Combat.TargetId ?? "-"}
   Condition  {snapshot.Combat.TargetCondition ?? "-"}
   Out        {lastPlayerDamage}
   In         {lastOpponentDamage}
 """;
    }

    private void RenderAuthority(JevAuthoritySnapshot snapshot, JevDecisionTrace? decision)
    {
        StringBuilder text = new();
        text.AppendLine($"Preset: {snapshot.Preset}");
        foreach (KeyValuePair<JevDomain, JevAuthority> pair in snapshot.Domains)
        {
            text.AppendLine($"{pair.Key,-11} {pair.Value}");
        }

        text.AppendLine();
        if (decision is not null)
        {
            AppendDecision(text, decision);
        }
        else
        {
            text.AppendLine("No Jev decision yet.");
            text.AppendLine(_runtime.DecisionEngine is null
                ? "Set TYPESAFE_API_KEY to enable Jev."
                : "Jev API configured. Combat triggers remain explicit.");
        }

        _jevView.Text = text.ToString();
        RefreshHeader();
    }

    private void RenderDecision(JevDecisionTrace decision)
    {
        RenderAuthority(_runtime.Authority.Current, decision);
    }

    private void RefreshHeader()
    {
        StateSnapshot snapshot = _lastSnapshot;
        string connection = snapshot.Session.ConnectionStatus.ToString().ToUpperInvariant();
        string room = snapshot.Room.Name ?? "-";
        string vitals = $"HP {FormatCompact(snapshot.Character.HitPoints)}  MN {FormatCompact(snapshot.Character.Mana)}  MV {FormatCompact(snapshot.Character.Movement)}";
        string scroll = _gameView.IsFollowingTail
            ? "LIVE"
            : $"SCROLL +{_gameView.LinesBelow}";
        string pager = _pagerWaiting ? " | ENTER: continue" : string.Empty;
        string inputMode = snapshot.Session.InputMode == SessionInputMode.Normal
            ? string.Empty
            : $" | INPUT {snapshot.Session.InputMode}";
        string capture = snapshot.Session.ActiveCapture == ResponseCaptureKind.None
            ? string.Empty
            : $" | CAPTURE {snapshot.Session.ActiveCapture}";

        _header.Text =
            $"{connection} | {room} | {vitals} | JEV {_runtime.Authority.Current.Preset} | {scroll}{pager}{inputMode}{capture} | PgUp/PgDn scroll  Ctrl+G live  F4 Jev  Ctrl+Q quit";
    }

    protected override bool OnKeyDown(Key key)
    {
        if (key == Key.F4)
        {
            OpenJevSettings();
            return true;
        }

        return base.OnKeyDown(key);
    }

    private void OpenJevSettings()
    {
        IApplication? app = App;
        if (app is null)
        {
            return;
        }

        using JevSettingsDialog dialog = new(_runtime.Authority.Current);
        app.Run(dialog);
        JevAuthoritySnapshot? result = dialog.Result;
        _input.SetFocus();

        if (result is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _runtime.ApplyAndSaveAuthorityAsync(result, _viewCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_viewCts.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                AppendSystemMessage($"Jev settings failed: {exception.Message}");
            }
        }, CancellationToken.None);
    }

    private static void AppendDecision(StringBuilder text, JevDecisionTrace decision)
    {
        text.AppendLine($"Decision {decision.DecisionId.ToString()[..8]}");
        text.AppendLine($"Domain   {decision.Domain}");
        text.AppendLine($"State    v{decision.StateVersion}");
        text.AppendLine($"Model    {decision.Model}");
        text.AppendLine($"Latency  {(decision.CompletedAt - decision.StartedAt).TotalMilliseconds:F0} ms");
        text.AppendLine($"Goal     {decision.Goal}");
        text.AppendLine();
        text.AppendLine("Candidates");

        foreach (DecisionCandidate candidate in decision.Candidates.Take(8))
        {
            text.AppendLine($" {candidate.Probability,6:P1}  {candidate.Action}");
        }

        text.AppendLine();
        text.AppendLine($"Selected   {decision.Selected.Action}");
        text.AppendLine($"Confidence {decision.Confidence:P1}");
    }

    private static string FormatEvent(EventEnvelope envelope)
    {
        string detail = envelope.Payload switch
        {
            CombatDamageObserved damage =>
                $"{damage.Damage.Source} {damage.Damage.AbsoluteTerm ?? "-"} {damage.Damage.DamageType} {damage.Damage.RelativeTerm}",
            CombatTargetConditionObserved condition =>
                $"{condition.TargetName}: {condition.Condition}",
            EnemyKilled killed => killed.TargetName,
            ExperienceGained xp => $"+{xp.Amount} xp",
            ExplorationGained exploration => $"+{exploration.Amount} explore, +{exploration.ExperienceAmount} xp",
            CurrencyGained currency => $"+{currency.Amount} {currency.Currency}",
            CharacterPromoted promoted => $"+{promoted.HitPointGain} hp, +{promoted.ManaGain} mana, +{promoted.MovementGain} move",
            SessionInputModeChanged mode => mode.Mode.ToString(),
            NavigationAttempted navigation => navigation.Direction,
            NavigationFailed failed => failed.Direction is null ? failed.Reason : $"{failed.Direction}: {failed.Reason}",
            RoomExitStateChanged exit => $"{exit.Direction} {exit.DoorState}/{exit.Traversability}",
            RoomObservationObserved room => $"{room.RoomName}: {room.Contents.Count} contents, {room.ExitDetails.Count} exits",
            CombatAttackObserved attack => $"{attack.Attack.Source} {attack.Attack.AttackType ?? "attack"} {attack.Attack.Outcome}",
            EquipmentChanged equipment => $"{equipment.Slot} = {equipment.Item ?? "-"}",
            CharacterConditionChanged condition => $"{condition.Condition} {(condition.Active ? "on" : "off")}",
            SkillImproved skill => skill.SkillName,
            SkillPracticeSucceeded skill => skill.SkillName,
            SkillsSnapshotObserved skills => $"{skills.Skills.Count} skills ({skills.Completeness})",
            SpellsSnapshotObserved spells => $"{spells.Spells.Count} spells ({spells.Completeness})",
            CommunicationObserved communication => $"{communication.Channel}: {communication.Speaker ?? "system"}",
            RoomContentsObserved contents =>
                $"{contents.Occupants.Count} occupants, {contents.Objects.Count} objects, {contents.UnknownContents.Count} unknown",
            RoomOccupantDeparted departed =>
                departed.Direction is null ? departed.TargetName : $"{departed.TargetName} -> {departed.Direction}",
            ActionExecuted action => action.Command.Length == 0 ? "<enter>" : action.Command,
            ActionRejected rejected => rejected.Reason,
            ComponentError error => $"{error.Component}: {error.Message}",
            JevEvaluationFailed failed => failed.Message,
            _ => string.Empty
        };

        return detail.Length == 0
            ? $"{envelope.Timestamp:HH:mm:ss.fff} {envelope.Payload.GetType().Name}"
            : $"{envelope.Timestamp:HH:mm:ss.fff} {envelope.Payload.GetType().Name}  {detail}";
    }

    private static bool ShouldDisplayEvent(IMudEvent mudEvent) =>
        mudEvent is not TextReceived and
        not GameTextReceived and
        not GameObservationReceived and
        not GmcpMessageReceived and
        not CharacterPromptObserved;

    private static string FormatExits(ExitState exits)
    {
        if (!exits.IsKnown)
        {
            return "unknown";
        }

        if (exits.Directions.Count == 0 && (exits.Details is null || exits.Details.Count == 0))
        {
            return "none";
        }

        if (exits.Details is null || exits.Details.Count == 0)
        {
            return string.Join(", ", exits.Directions);
        }

        return string.Join(", ", exits.Details.Select(exit =>
            exit.Traversability == ExitTraversability.Blocked
                ? $"{exit.Direction}(blocked)"
                : exit.Direction));
    }

    private static string FormatWealth(InventorySummary inventory)
    {
        List<string> parts = [];
        if (inventory.Gold is not null)
        {
            parts.Add($"{inventory.Gold}g");
        }
        if (inventory.Silver is not null)
        {
            parts.Add($"{inventory.Silver}s");
        }
        if (inventory.Copper is not null)
        {
            parts.Add($"{inventory.Copper}c");
        }
        return parts.Count == 0 ? "-" : string.Join(" ", parts);
    }

    private static string FormatPair(VitalState vital) =>
        vital.Current is null && vital.Maximum is null
            ? "-"
            : $"{vital.Current?.ToString() ?? "?"} / {vital.Maximum?.ToString() ?? "?"}";

    private static string FormatCompact(VitalState vital) =>
        vital.Current is null
            ? "-"
            : vital.Maximum is null
                ? vital.Current.Value.ToString()
                : $"{vital.Current}/{vital.Maximum}";

    private static string FormatRoomContents(
        IReadOnlyList<RoomContentObservation> contents,
        int maxItems)
    {
        if (contents.Count == 0)
        {
            return "    -";
        }

        StringBuilder text = new();
        foreach (RoomContentObservation content in contents.Take(maxItems))
        {
            text.Append("    ").AppendLine(FormatRoomContentDescription(content.Description));
        }

        if (contents.Count > maxItems)
        {
            text.Append("    +").Append(contents.Count - maxItems).Append(" more");
        }

        return text.ToString().TrimEnd();
    }

    private static string FormatRoomContentDescription(string description)
    {
        const int maxLength = 46;
        return description.Length <= maxLength
            ? description
            : description[..(maxLength - 3)] + "...";
    }

    private static string FormatDamage(DamageObservation? damage)
    {
        if (damage is null)
        {
            return "-";
        }

        string absolute = damage.AbsoluteTerm is null
            ? string.Empty
            : $"{damage.AbsoluteTerm}({damage.AbsoluteTier}) ";
        return $"{absolute}{damage.DamageType} {damage.RelativeTerm}({damage.RelativeTier})";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runtime.Interaction.World.Appended -= HandleWorldBufferAppended;
            _runtime.Interaction.RuleDiagnostic -= HandleOutputRuleDiagnostic;
            _submittedCommands.Writer.TryComplete();
            _viewCts.Cancel();
            _viewCts.Dispose();
        }

        base.Dispose(disposing);
    }
}
