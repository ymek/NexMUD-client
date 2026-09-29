#!/usr/bin/env python3
from __future__ import annotations

import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
errors: list[str] = []


def fail(message: str) -> None:
    errors.append(message)


xml_files = [ROOT / "Directory.Build.props", ROOT / "NexMud.slnx", *ROOT.glob("src/**/*.csproj"), *ROOT.glob("tests/**/*.csproj")]

# Product identity is NexMud/NexMUD. Jev remains valid only as the explicit
# decision/agent feature name, never as the repository/project namespace prefix.
legacy_product_tokens = ("Jev" + "Mud", "Jev" + "MUD", "jev" + "mud")
legacy_scan_roots = (ROOT / "src", ROOT / "tests", ROOT / "scripts", ROOT / "docs")
legacy_scan_files = [ROOT / "README.md", ROOT / "IMPLEMENTATION_NOTES.md", ROOT / "Directory.Build.props", ROOT / "NexMud.slnx"]
for scan_root in legacy_scan_roots:
    for path in scan_root.rglob("*"):
        if any(part in {"bin", "obj", "artifacts"} for part in path.parts):
            continue
        if any(token in path.name for token in legacy_product_tokens):
            fail(f"legacy pre-rename product identity remains in path: {path.relative_to(ROOT)}")
        if path.is_file():
            legacy_scan_files.append(path)
for path in legacy_scan_files:
    try:
        text = path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        continue
    for token in legacy_product_tokens:
        if token in text:
            fail(f"legacy pre-rename product identity remains in {path.relative_to(ROOT)}: {token}")
for path in xml_files:
    try:
        ET.parse(path)
    except Exception as exc:  # noqa: BLE001 - verification should report all malformed XML
        fail(f"invalid XML: {path.relative_to(ROOT)}: {exc}")

for project in [*ROOT.glob("src/**/*.csproj"), *ROOT.glob("tests/**/*.csproj")]:
    tree = ET.parse(project)
    for reference in tree.findall(".//ProjectReference"):
        include = reference.get("Include")
        if not include:
            fail(f"ProjectReference without Include: {project.relative_to(ROOT)}")
            continue
        target = (project.parent / include).resolve()
        if not target.is_file():
            fail(f"missing ProjectReference target: {project.relative_to(ROOT)} -> {include}")

solution = ET.parse(ROOT / "NexMud.slnx")
for project in solution.findall(".//Project"):
    path = project.get("Path")
    if not path or not (ROOT / path).is_file():
        fail(f"solution references missing project: {path!r}")

for executable_project in (
    ROOT / "src/NexMud.Tui/NexMud.Tui.csproj",
    ROOT / "src/NexMud.Gui/NexMud.Gui.csproj",
):
    tree = ET.parse(executable_project)
    output_types = [node.text for node in tree.findall(".//OutputType")]
    if "Exe" not in output_types:
        fail(f"{executable_project.parent.name} must declare OutputType=Exe")

packages = []
for project in ROOT.glob("src/**/*.csproj"):
    tree = ET.parse(project)
    for package in tree.findall(".//PackageReference"):
        packages.append((project, package.get("Include"), package.get("Version")))
expected_packages = {
    (ROOT / "src/NexMud.Tui/NexMud.Tui.csproj", "Terminal.Gui", "2.5.0"),
    (ROOT / "src/NexMud.Gui/NexMud.Gui.csproj", "Avalonia", "12.1.2"),
    (ROOT / "src/NexMud.Gui/NexMud.Gui.csproj", "Avalonia.Desktop", "12.1.2"),
    (ROOT / "src/NexMud.Gui/NexMud.Gui.csproj", "Avalonia.Themes.Fluent", "12.1.2"),
    (ROOT / "src/NexMud.Client/NexMud.Client.csproj", "Microsoft.Data.Sqlite", "10.0.12"),
    (ROOT / "src/NexMud.Scripting.Jint/NexMud.Scripting.Jint.csproj", "Jint", "4.16.3"),
}
if set(packages) != expected_packages:
    rendered = sorted((str(p.relative_to(ROOT)), name, version) for p, name, version in packages)
    fail(f"unexpected external package set: {rendered}")

source = "\n".join(path.read_text(encoding="utf-8") for path in [*ROOT.glob("src/**/*.cs"), *ROOT.glob("tests/**/*.cs")])
for forbidden in (
    "Application.Init(",
    "Application.Run(",
    "Application.Shutdown(",
    "Application.Top",
    "NotImplementedException",
    "TODO",
    "FIXME",
    "OnDrawingContent",
    "OnDrawContent",
):
    if forbidden in source:
        fail(f"forbidden/stale source marker found: {forbidden}")

required_literals = {
    "TypeSafe endpoint": '"v1/systemone"',
    "lossless UI event feed": "runtime.Events.SubscribeLossless()",
    "state broadcast subscription": "runtime.State.Subscribe()",
    "Ctrl+Q application quit binding": "Application.DefaultKeyBindings[Command.Quit] = Bind.All(Key.Q.WithCtrl);",
    "ANSI game renderer": "new AnsiConsoleView",
    "Terminal.Gui v2 drawing event": "DrawingContent += (_, args) =>",
    "Terminal.Gui v2 draw cancellation": "args.Cancel = true;",
    "ordered UI command queue": "Channel.CreateBounded<string>",
    "Avendar adapter": "new AvendarGameAdapter",
    "action-correlated navigation response": "NavigationResponseCompleted",
    "navigation attempt action correlation": "new NavigationAttempted(direction, action.ActionId)",
    "Avendar room contents parser": "AvendarRoomContentsParser _roomContentsParser",
    "room occupants state": "IReadOnlyList<RoomContentObservation> Occupants",
    "Avendar compact telemetry prompt": "prompt [J|%h/%H|%m/%M|%v/%V|%x|%X|%s|%r|%e|%T|%d]",
    "pre-send action observability": "new ActionDispatching",
    "persistent client settings": "ClientSettingsStore",
    "persistent Jev save path": "ApplyAndSaveAuthorityAsync",
    "full skill catalog state": "SkillsCompleteness",
    "spell catalog state": "SpellsCompleteness",
    "response capture ownership": "ResponseCaptureKind",
    "game-first GUI entrypoint": "StartWithClassicDesktopLifetime",
    "GUI game console": "SelectableTextBlock",
    "GUI contextual world dock": "BuildContextPanel",
    "GUI persistent character pane": "CharacterHudPanel",
    "GUI skill drawer": "BuildSkillsPanel",
    "GUI spell drawer": "BuildSpellsPanel",
    "GUI settings workspace": "class SettingsWorkspace",
    "GUI combat HUD": "BuildCombatHud",
    "GUI combat HUD MA abbreviation": 'SetVital(_mana, _manaLabel, "MA", state.Character.Mana);',
    "lossless raw display publication": 'new GameTextReceived(rawText)',
    "split-view transcript": 'Text = "LIVE OUTPUT"',
    "transcript logger": 'TranscriptLogWriter _logWriter',
    "typed output transformation pipeline": "class OutputTransformationService",
    "presentation output rules editor": "class OutputRulesEditor",
    "macOS application name": 'Name = "NexMUD"',
    "neutral telnet identity": 'TerminalType = "xterm-256color"',
    "Jev typed question model": "JevQuestionType",
    "Jev Choice question": 'new SystemOneQuestion("choice"',
    "Jev Score question": 'new SystemOneQuestion("score"',
    "Jev Noul question": 'new SystemOneQuestion("noul"',
    "Jev parallel combat danger score": 'JevAuxiliaryQuestion.Score(',
    "Jev parallel combat disengage noul": 'JevAuxiliaryQuestion.Noul(',
    "Jev operational coordinator": "class JevDecisionCoordinator",
    "nullable channel TryRead": "TryRead(out EvaluationSignal? signal)",
    "Jev approval flow": "JevApprovalRequested",
    "Jev current-state rematerialization": "TryMaterializeCombatAction",
    "Jev semantic action ids": "string DecisionAction",
    "Jev semantic action choice surface": "criteria[definition.DecisionAction]",
    "Jev score rubric owned by request": "question.ScoreCriteria",
    "GUI Jev System One presentation": 'Text = "System One decision engine"',
    "GUI Jev Choice distribution": 'CollapsibleSection("Action distribution"',
    "GUI Jev Score/Noul presentation": 'CollapsibleSection("Parallel evaluations"',
    "GUI Jev decision history": 'CollapsibleSection("Recent decisions"',
    "command aliases": "CommandAliasExpander",
    "deterministic trigger/timer automation": "ClientAutomationService",
    "language-neutral scripting contracts": "interface IScriptRuntime",
    "script host capability boundary": "interface IScriptHost",
    "script execution ownership": "interface IScriptExecutionScope",
    "script scheduler abstraction": "interface IScriptScheduler",
    "script event subscription abstraction": "interface IScriptEventSubscription",
    "stable script event names": "class ScriptEventTypes",
    "runtime task fault observability": "record ScriptTaskFault",
    "namespaced script storage": "interface IScriptStorage",
    "capability permission model": "enum ScriptCapability",
    "managed bootstrap runtime": 'RuntimeName => "managed-bootstrap"',
    "command provenance": "record CommandProvenance",
    "automation shared command host": "IScriptCommands _commands",
    "automation shared scheduler": "IScriptScheduler _scheduler",
    "mapper shared execution scope": "ScriptOwnerKind.MapperRoute",
    "scripting platform composition root": "class ClientScriptPlatform",
    "automation-owned behavior workspace": "class AutomationWorkspace",
    "logical scrollback search": "WorldBufferSearchOptions",
    "command palette": 'Title = "NexMUD Command Palette"',
    "plain transcript log format": "TranscriptLogFormat.PlainText",
    "json transcript log format": "TranscriptLogFormat.JsonLines",
    "workspace persistence": "SaveWorkspaceAsync",
    "async macOS shutdown": "desktop.ShutdownRequested += HandleShutdownRequested",
    "bounded runtime shutdown": "WaitAsync(TimeSpan.FromSeconds(3)",
    "durable knowledge database": "class WorldKnowledgeStore",
    "durable generic event archive": "CREATE TABLE IF NOT EXISTS mud_events",
    "cross-session event identity": "event_id TEXT NOT NULL UNIQUE",
    "durable room memory": "CREATE TABLE IF NOT EXISTS rooms",
    "durable combat memory": "CREATE TABLE IF NOT EXISTS combat_events",
    "durable command history": "CREATE TABLE IF NOT EXISTS command_history",
    "knowledge DB busy timeout": "PRAGMA busy_timeout=2000",
    "knowledge DB transient failure resilience": "catch (SqliteException) when (!cancellationToken.IsCancellationRequested)",
    "Jev persistent context": "IReadOnlyList<ExecutedCommandKnowledge> RecentCommands",
    "source-aware executed command history": "CREATE TABLE IF NOT EXISTS action_history",
    "source-aware action events": "DecisionSource Source = DecisionSource.Human",
    "provenance-aware local echo": "AppendCommandEcho(ActionDispatching action, DateTimeOffset timestamp, bool showProvenance)",
    "Jev-labeled automated command echo": '\"[Jev] > \"',
    "normalized semantic help prose": "NormalizeWhitespace(string.Join(\" \", description))",
    "knowledge codex": "BuildKnowledgePanel",
    "persistent command history service": "Interaction.Input.RestoreHistoryAsync",
    "contextual tab completion service": "class CompletionService",
    "configurable command hotkeys": "CommandKeyBinding",
    "flat command palette": 'Text = "COMMAND PALETTE"',
    "macOS primary shortcut support": "KeyModifiers.Meta",
    "base HUD Jev glance": 'BuildHudReadout("Jev", _jevHudLabel',
    "presentation-only prompt slurp setting": "SlurpTelemetryPrompt",
    "fragment-safe prompt display filter": "class AvendarPromptDisplayFilter",
    "Jev navigation projector": "TryCreateNavigationRequest",
    "Jev recovery projector": "TryCreateRecoveryRequest",
    "lossless Jev control feed": "_jevEvents = Events.SubscribeLossless()",
    "authoritative state catch-up watermark": "LastProcessedSequence",
    "single in-flight autonomy action": "PendingAutonomyAction? _pendingAction",
    "autonomy outcome timeout watchdog": "WatchPendingOutcomeAsync",
    "navigation immediate-backtrack guard": "ImmediateBacktrackDirection",
    "autonomy recent-room tabu context": "RecentRoomIds",
    "blind grind social target protection": "ProtectedTargets",
    "server posture feedback repair": "CharacterPositionObserved",
    "Jev generic domain materialization": "TryMaterializeAction(",
    "provenance-aware bright command echo": "AnsiColor echoColor = origin switch",
    "custom macOS About menu": 'NativeMenuItem about = new("About NexMUD…")',
    "persistent World transcript and command input": 'RowDefinitions = new RowDefinitions("Auto,*,Auto")',
    "flat abilities surface": 'ToolView.Abilities or ToolView.Skills or ToolView.Spells => BuildAbilitiesPanel',
    "item identification parser": "class AvendarItemIdentificationParser",
    "ability help parser": "class AvendarAbilityHelpParser",
    "durable item knowledge": "CREATE TABLE IF NOT EXISTS item_knowledge",
    "durable ability help": "CREATE TABLE IF NOT EXISTS ability_help",
    "item references in Jev context": "IReadOnlyList<ItemKnowledge> EquippedItems",
    "ability references in Jev context": "IReadOnlyList<AbilityHelpKnowledge> AbilityReference",
    "response output excluded from room classification": "claimedByAnotherParser: _responseMode != ResponseMode.None",
    "leading telemetry prompt response guard": "Do not terminate a response capture until we have actually observed response text.",
    "interactive equipment identification": 'SubmitCommandAsync($"id {slot.Item}")',
    "interactive ability help": 'SubmitCommandAsync($"help {name}")',
    "identified equipment state": "IdentifiedItems",
    "identified weapon legality": "EquippedWeaponIsBlunt(state)",
    "shared GUI design language": "internal static class UiTheme",
    "compact Fluent control density": "DensityStyle = DensityStyle.Compact",
    "persistent sidebar disclosure state": "_sectionExpansion",
    "integrated right workspace host": "ContentControl _workspaceHost",
    "flat sidebar section primitive": "SectionBlock(",
    "rich equipment hover reference": "BuildEquipmentTooltip(",
    "persistent equipment hover memory": "WarmEquipmentKnowledgeAsync",
    "persistent ability hover memory": "WarmAbilityKnowledgeAsync",
    "ability information grid": "BuildAbilityGrid(",
    "ability progression preview": "BuildProgressionPreview(",
    "world information table": "BuildRoomContentsTable(",
    "world durable room memory": 'SectionBlock("Room memory"',
    "codex browse workspace": 'Search rooms, MOBs, items, abilities, combat…',
    "visual map graph": "class MapperViewport : Control",
    "persistent mapper workspace": "class MapperWorkspace : UserControl",
    "read-only mapper repository": "class MapperReadRepository",
    "persistent codex workspace": "class CodexWorkspace : UserControl",
    "mapper entity destination search": "MapperDestinationKind.Entity",
    "controlled mapper auto-move": "class AutoMoveService",
    "auto-move arrival gating": "one edge at a time",
    "auto-move arrival timeout": "AutoMoveStepTimeoutMilliseconds",
    "Jev master control": "Master Jev control",
    "Jev master execution gate": "Jev is disabled by the master control.",
    "codex searchable persistence": "SearchCodexAsync",
    "persisted extended item fields": "IReadOnlyDictionary<string, string> ExtraFields",
    "persisted extended ability fields": "IReadOnlyDictionary<string, string> Fields",
}
for name, literal in required_literals.items():
    if literal not in source:
        fail(f"missing {name}: {literal}")

knowledge_store_source = (ROOT / "src/NexMud.Client/Knowledge/WorldKnowledgeStore.cs").read_text()
knowledge_store_required_api = {
    "codex search API": "public async Task<IReadOnlyList<CodexEntrySummary>> SearchCodexAsync(",
    "codex detail API": "public async Task<CodexEntryDetail?> GetCodexEntryAsync(",
    "room metadata read API": "public async Task<MapperRoomMetadata?> GetRoomMetadataAsync(",
    "mapper recording preference gate": "private bool MapperRecordingEnabled()",
    "mapper persistence preference gate": "private bool MapperPersistenceEnabled()",
}
for name, declaration in knowledge_store_required_api.items():
    if declaration not in knowledge_store_source:
        fail(f"missing {name}: {declaration}")

# File-specific GUI compile/layout regressions. These symbols live outside the
# namespaces imported by the original generated source, and Avalonia Grid does
# not create implicit rows for Grid.Row assignments.
gui_required_literals = {
    ROOT / "src/NexMud.Gui/App.cs": [
        "using Avalonia.Controls;",
        "using Avalonia.Threading;",
        "Dispatcher.UIThread.UnhandledException += HandleUnhandledUiException;",
        "CrashDiagnostics.Record(\"Unhandled Avalonia UI exception\"",
        "ShutdownMode.OnExplicitShutdown",
        "Dispatcher.UIThread.Post(() => desktop.Shutdown(0))",
        'NativeMenuItem about = new("About NexMUD…")',
        "NativeMenu.SetMenu(this, menu)",
    ],
    ROOT / "src/NexMud.Gui/MainWindow.cs": [
        "using Avalonia.Controls.Primitives;",
        "Dispatcher.UIThread.Post(() => _searchQuery.Focus());",
    ],
    ROOT / "src/NexMud.Gui/SettingsWorkspace.cs": [
        "using Avalonia.Controls.Primitives;",
        "SettingsPage.Connections",
        "SettingsPage.DataLogging",
    ],
}
for path, literals in gui_required_literals.items():
    text = path.read_text(encoding="utf-8")
    for literal in literals:
        if literal not in text:
            fail(f"missing GUI compile/layout requirement in {path.relative_to(ROOT)}: {literal}")

main_window_text = (ROOT / "src/NexMud.Gui/MainWindow.cs").read_text(encoding="utf-8")
settings_workspace_text = (ROOT / "src/NexMud.Gui/SettingsWorkspace.cs").read_text(encoding="utf-8")

# Explorer workspaces must remain outside MainWindow's render/rebuild lifecycle.
main_window_explorer_forbidden = (
    "QueueCodexSearch(",
    "RefreshMapperGraphAsync(",
    "BuildVisualMap(",
    "_codexDetailHost",
    "_mapperGraphLoading",
)
for literal in main_window_explorer_forbidden:
    if literal in main_window_text:
        fail(f"explorer subsystem leaked back into MainWindow render lifecycle: {literal}")

codex_workspace_text = (ROOT / "src/NexMud.Gui/CodexWorkspace.cs").read_text(encoding="utf-8")
mapper_workspace_text = (ROOT / "src/NexMud.Gui/MapperWorkspace.cs").read_text(encoding="utf-8")
mapper_repository_text = (ROOT / "src/NexMud.Client/Knowledge/MapperReadRepository.cs").read_text(encoding="utf-8")
mapper_topology_text = (ROOT / "src/NexMud.Client/Knowledge/MapperTopologyLayout.cs").read_text(encoding="utf-8")
if "RenderDock(" in codex_workspace_text or "RenderDock(" in mapper_workspace_text:
    fail("explorer workspaces must not depend on MainWindow.RenderDock")
if "_resultList.SelectionChanged" in mapper_workspace_text:
    fail("mapper search results must not use ListBox selection semantics")
if "private readonly ListBox _resultList" in mapper_workspace_text:
    fail("mapper search results must use explicit action rows rather than a ListBox selection model")
if "BuildSearchResultButton(" not in mapper_workspace_text:
    fail("mapper search results must expose explicit click actions")
if "_results.SelectionChanged += async" in codex_workspace_text:
    fail("codex selection must not use an async SelectionChanged handler")
if "HandleEntrySelection(entry);" not in codex_workspace_text:
    fail("codex selection exception boundary is missing")
for literal in ("ClearSearchAfterSelection()", "BuildSearchResultButton(", "HandleDestinationSelection(entry);"):
    if literal not in mapper_workspace_text:
        fail(f"mapper destination selection crash guard missing: {literal}")
for literal in (
    "ResetCancellationTokenSource(ref _searchCts);",
    "ResetCancellationTokenSource(ref _graphCts);",
    "ResetCancellationTokenSource(ref _routeCts);",
):
    if literal not in mapper_workspace_text:
        fail(f"mapper cancellation-token lifecycle guard missing: {literal}")
if "private static void ResetCancellationTokenSource(ref CancellationTokenSource? source)" not in mapper_workspace_text:
    fail("mapper cancellation-token reset helper is missing")

if "ClearSearchAfterSelection();\n        await PlanRouteAsync()" not in mapper_workspace_text:
    fail("mapper selection must clear its originating query before asynchronous route planning")
if "await PlanRouteAsync().ConfigureAwait(true);\n        ClearSearchAfterSelection();" in mapper_workspace_text:
    fail("stale route planning may not clear a newer mapper search")
interaction_input_text = (ROOT / "src/NexMud.Client/Interaction/InputInteraction.cs").read_text(encoding="utf-8")
interaction_output_text = (ROOT / "src/NexMud.Client/Interaction/OutputInteraction.cs").read_text(encoding="utf-8")
interaction_keybinding_text = (ROOT / "src/NexMud.Client/Interaction/Keybindings.cs").read_text(encoding="utf-8")
for literal in ("RenderedTranscriptSegmentLimit", "TrimRenderedTranscriptIfNeeded()", "_runtime.Interaction.World.Appended += HandleWorldBufferAppended"):
    if literal not in main_window_text:
        fail(f"logical transcript projection invariant missing: {literal}")
for literal in ("class CommandHistoryService", "class CompletionService", "class InputPipeline", "CommandBatchSplitter.Split", "_aliases.TryResolveAsync"):
    if literal not in interaction_input_text:
        fail(f"interaction input ownership invariant missing: {literal}")
for literal in ("record OutputFrame", "class OutputTransformationService", "class WorldBuffer", "SourceFrames.Append(frame)", "CreatePresentation(frame)"):
    if literal not in interaction_output_text:
        fail(f"interaction output ownership invariant missing: {literal}")
if "case GameTextReceived game:" in main_window_text and "ProcessServerOutput" in main_window_text:
    fail("Avalonia must project WorldBuffer rather than transform GameTextReceived itself")
if "_completionTokenRecency" in main_window_text or "BuildCompletionCandidates" in main_window_text:
    fail("Avalonia must not retain UI-local completion ownership")
if "_runtime.Interaction.Frames.Reset()" in main_window_text:
    fail("Avalonia must not own output parser/transformation lifecycle resets")
if ".LoadAsync(preferences.HistoryMaximumEntries" not in interaction_input_text:
    fail("persistent history restore must honor the configured history capacity")
if "PublishAppended(entry)" not in interaction_output_text or "GetInvocationList()" not in interaction_output_text:
    fail("logical output/view notifications must isolate subscriber faults from the output worker")
if "FindMatches(matcher, rendered)" not in interaction_output_text:
    fail("highlight matching must use the full rendered line so ANSI run boundaries do not break matches")
contracts_actions_text = (ROOT / "src/NexMud.Contracts/Actions/MudActions.cs").read_text(encoding="utf-8")
script_host_contracts_text = (ROOT / "src/NexMud.Scripting/Host/ScriptHostContracts.cs").read_text(encoding="utf-8")
for literal in ("User = 0", "Automation = 1", "Jev = 2", "Mapper = 3", "Script = 4", "System = 5", "Alias = 6", "Keybinding = 7"):
    if literal not in contracts_actions_text or literal not in script_host_contracts_text:
        fail(f"command provenance enum compatibility invariant missing: {literal}")
contracts_events_text = (ROOT / "src/NexMud.Contracts/Events/MudEvents.cs").read_text(encoding="utf-8")
adapter_text = (ROOT / "src/NexMud.Adapters/Avendar/AvendarGameAdapter.cs").read_text(encoding="utf-8")
knowledge_text = (ROOT / "src/NexMud.Client/Knowledge/WorldKnowledgeStore.cs").read_text(encoding="utf-8")
for literal in ("AreaObserved(string Area)",):
    if literal not in contracts_events_text:
        fail("explicit area observation contract is missing")
for literal in ('normalized.Equals("where"', "TryParseWhereArea(", "new AreaObserved(area)"):
    if literal not in adapter_text:
        fail(f"Avendar explicit area detection is missing: {literal}")
if "case AreaObserved area" not in knowledge_text or "PersistExplicitAreaAsync" not in knowledge_text:
    fail("explicit area observations must persist to the current room only")

if "FindFree(" in mapper_workspace_text:
    fail("mapper may not relocate cardinal rooms to arbitrary free coordinates")
for literal in (
    "class MapperTopologyLayout",
    "Coordinates are intentionally non-authoritative",
    "FindDirectionalSlot",
    "FindPortalSlot",
    "ConstraintConflictCount",
):
    if literal not in mapper_topology_text:
        fail(f"MUD topology/presentation invariant missing: {literal}")
for forbidden in (
    'return "TOPOLOGY !";',
    "from + offset != to",
    "UP/DOWN are stacked",
):
    if forbidden in mapper_workspace_text or forbidden in mapper_topology_text:
        fail(f"Euclidean mapper assumption returned: {forbidden}")
for literal in (
    "PendingTraversalLifetime",
    "SourceRoomId",
    "RebaseQueuedTraversalBatch",
    "CREATE TABLE IF NOT EXISTS room_transitions",
    '"relocation"',
):
    if literal not in knowledge_text:
        fail(f"movement transition safety invariant missing: {literal}")
for literal in (
    "from_room_id IN ({roomSet}) OR to_room_id IN ({roomSet})",
    "door_state, block_reason",
    "unexplored_directions",
):
    if literal not in mapper_repository_text:
        fail(f"mapper neighborhood/edge-state invariant missing: {literal}")
for literal in (
    "MapperKnowledgeChanged",
    "HandleMapperKnowledgeChanged",
    "ExitDoorState.Locked",
):
    if literal not in mapper_workspace_text and literal not in knowledge_text:
        fail(f"mapper live topology/visual-state invariant missing: {literal}")
for literal in ('"up" => "U"', '"down" => "D"'):
    if literal not in mapper_topology_text:
        fail(f"mapper vertical direction label invariant missing: {literal}")

for literal in (
    ".GroupBy(edge => ConnectionKey(edge.FromRoomId, edge.ToRoomId), StringComparer.Ordinal)",
    "DrawDirectionalEdgeState(context, edge, edgeStart, edgeEnd, EdgeBrush(edge, route));",
    "private static string ConnectionKey(string firstRoomId, string secondRoomId)",
):
    if literal not in mapper_workspace_text:
        fail(f"mapper reciprocal-edge rendering invariant missing: {literal}")
if "context.DrawLine(new Pen(edgeBrush" in mapper_workspace_text:
    fail("directed blocked state must not paint an entire reciprocal room connection")

client_script_infrastructure_text = (ROOT / "src/NexMud.Client/Scripting/ClientScriptInfrastructure.cs").read_text(encoding="utf-8")
if "ArgumentException.ThrowIfNullOrWhiteSpace(request.Command)" in client_script_infrastructure_text:
    fail("shared command adapter must permit empty MUD input")
if "ArgumentNullException.ThrowIfNull(request.Command)" not in client_script_infrastructure_text:
    fail("shared command adapter should reject null command values without rejecting empty input")

automation_workspace_text = (ROOT / "src/NexMud.Gui/AutomationWorkspace.cs").read_text(encoding="utf-8")
scripting_workspace_text = (ROOT / "src/NexMud.Gui/ScriptingWorkspace.cs").read_text(encoding="utf-8")
for literal in (
    'Text = "AUTOMATION"',
    '"Aliases", "Keybindings", "Triggers", "State Rules", "Workflows", "Timers", "Script-backed"',
    'UiTheme.PrimaryButton("New Automation")',
    'BuildKeybindingEditor',
    'BuildStateRuleEditor',
    'BuildPreferences',
    'AutomationKeybindingInvoked',
):
    if literal not in automation_workspace_text:
        fail(f"user-facing automation workspace requirement missing: {literal}")
for legacy_literal in ('Panel("WORKFLOWS"', 'Panel("VARIABLES"'):
    if legacy_literal in automation_workspace_text:
        fail(f"operator-oriented automation UX returned: {legacy_literal}")
if "mudEvent is not AutomationRuleMatched and" in automation_workspace_text:
    fail("automation workspace event filter must use boolean && between independent type tests")
for literal in (
    "mudEvent is not AutomationRuleMatched &&",
    "mudEvent is not AutomationVariableChanged &&",
    "mudEvent is not AutomationWorkflowStateChanged &&",
    'mudEvent is not ScriptLogEmitted { ModuleId: "automation.profile" }',
):
    if literal not in automation_workspace_text:
        fail(f"automation workspace event filter invariant missing: {literal}")
if "SqliteOpenMode.ReadOnly" not in mapper_repository_text or "SqliteCacheMode.Private" not in mapper_repository_text:
    fail("mapper repository must use an isolated read-only private-cache SQLite connection")
if "UiTheme.Window" not in main_window_text or "UiTheme.Window" not in settings_workspace_text:
    fail("main and settings windows must use the shared UiTheme design language")
abilities_start = main_window_text.index("    private Control BuildAbilitiesPanel(CharacterState character)")
abilities_end = main_window_text.index("    private Control BuildSkillsPanel(CharacterState character)", abilities_start)
abilities_panel_text = main_window_text[abilities_start:abilities_end]
if ".Where(skill => skill.Availability == SkillAvailability.Available)" in abilities_panel_text:
    fail("Abilities panel must present the complete skill catalog, not only available skills")
if 'SectionBlock("Next unlocks"' in abilities_panel_text:
    fail("Abilities panel must integrate locked skills into the main information grid")
if "AbilityDisplayValue(skill.Availability, skill.RequiredLevel, skill.ProficiencyPercent)" not in abilities_panel_text:
    fail("Abilities panel must show proficiency for ready skills and unlock level/N/A for locked skills")

if "CornerRadius = new CornerRadius(7)" in settings_workspace_text:
    fail("settings editor rows regressed to rounded card chrome")
if 'UtilityButton("⚙", "Settings")' in main_window_text:
    fail("settings affordance regressed to an undersized icon-only control")
if '#43C7F4' in main_window_text or '#43C7F4' in settings_workspace_text:
    fail("legacy cyan GUI palette must not reappear outside user-configured highlights")
if '#B99A5B' in main_window_text or '#B99A5B' in settings_workspace_text or '#B99A5B' in (ROOT / "src/NexMud.Gui/UiTheme.cs").read_text(encoding="utf-8"):
    fail("legacy sepia/gold GUI accent must not reappear in the application design system")
if 'Text = room.Name ?? state.Room.Name ?? room.RoomId' in main_window_text:
    fail("GUI must never fall back to exposing internal room ids as room names")
for literal in (
    "CharacterHudPanel _characterHudPanel",
    "RoomContextPanel _roomContextPanel",
    "ContentControl _workspaceHost",
    "SettingsWorkspace? _settingsWorkspace",
    "RenderPersistentGameplay()",
    "EnsureRoomMetadata(_snapshot.Room.Id, force: true)",
    "string.Equals(memory?.RoomId, _snapshot.Room.Id, StringComparison.Ordinal)",
    "_workspaceColumn.MinWidth = 380",
    'NavigationButton("Scripting", ToolView.Scripting, NexIconKind.Scripting)',
    'new("NAVIGATE", "Scripting"',
    "ApplyWorkspaceLayout(ToolView view)",
    "_workspaceHost.Content = _mapperWorkspace",
    "_workspaceHost.Content = _codexWorkspace",
    "_workspaceHost.Content = _automationWorkspace",
    "_workspaceHost.Content = _scriptingWorkspace",
    "_settingsOverlay.IsVisible = true",
):
    if literal not in main_window_text:
        fail(f"single-window world-first GUI invariant missing: {literal}")
if '_dock.IsVisible = false;' in main_window_text:
    fail("persistent character/room gameplay rail may not be hidden by the main shell")
if 'AutoOpenCombatContext' in main_window_text:
    fail("legacy combat-context auto-open must not steal focus in the world-first shell")
if 'RowDefinitions = new RowDefinitions("Auto,*,Auto")' not in main_window_text:
    fail("main layout must retain app bar, world body, and status footer")

for required_gui_file in (
    ROOT / "src/NexMud.Gui/GameplayShellViewModels.cs",
    ROOT / "src/NexMud.Gui/NexMudDesignSystem.cs",
    ROOT / "src/NexMud.Gui/NexMudIcons.cs",
    ROOT / "src/NexMud.Gui/PersistentGameplayPanels.cs",
    ROOT / "src/NexMud.Gui/CharacterInventoryWorkspace.cs",
    ROOT / "src/NexMud.Gui/ItemInspectionPopover.cs",
    ROOT / "src/NexMud.Gui/SettingsWorkspace.cs",
    ROOT / "src/NexMud.Gui/ScriptingWorkspace.cs",
    ROOT / "src/NexMud.Gui/Assets/Textures/dark-metal.png",
    ROOT / "src/NexMud.Gui/Assets/Textures/obsidian-grain.png",
):
    if not required_gui_file.is_file():
        fail(f"missing world-first GUI component: {required_gui_file.relative_to(ROOT)}")

app_icon_png = ROOT / "src/NexMud.Gui/Assets/app-icon.png"
if not app_icon_png.is_file():
    fail("missing approved high-resolution application icon PNG")
else:
    data = app_icon_png.read_bytes()[:24]
    if len(data) < 24 or data[:8] != b"\x89PNG\r\n\x1a\n":
        fail("approved application icon must be a PNG")
    else:
        width, height = struct.unpack(">II", data[16:24])
        if width < 512 or height < 512:
            fail(f"approved application icon must be >=512px per side, found {width}x{height}")

if (ROOT / "src/NexMud.Gui/WorldVisualTheme.cs").exists():
    fail("legacy flat WorldVisualTheme must not survive the prescriptive design-system reconstruction")
if not (ROOT / "docs/architecture/NexMUD-avalonia-world-first-ui-architecture.md").is_file():
    fail("missing NexMUD Avalonia world-first UI architecture contract")
if not (ROOT / "docs/design/NexMUD-main-window-prescriptive-visual-spec.md").is_file():
    fail("missing prescriptive NexMUD main-window visual contract")
if not (ROOT / "docs/design/NexMUD-default-world-design-system-implementation.md").is_file():
    fail("missing default-World design-system implementation notes")

nex_design_text = (ROOT / "src/NexMud.Gui/NexMudDesignSystem.cs").read_text(encoding="utf-8")
nex_icons_text = (ROOT / "src/NexMud.Gui/NexMudIcons.cs").read_text(encoding="utf-8")
if "private readonly Avalonia.Controls.Shapes.Path _path;" not in nex_icons_text:
    fail("NexMUD icon library must fully qualify Avalonia.Controls.Shapes.Path")
if "_path = new Avalonia.Controls.Shapes.Path" not in nex_icons_text:
    fail("NexMUD icon construction must fully qualify Avalonia.Controls.Shapes.Path")
persistent_gameplay_text = (ROOT / "src/NexMud.Gui/PersistentGameplayPanels.cs").read_text(encoding="utf-8")
character_workspace_text = (ROOT / "src/NexMud.Gui/CharacterInventoryWorkspace.cs").read_text(encoding="utf-8")
item_popover_text = (ROOT / "src/NexMud.Gui/ItemInspectionPopover.cs").read_text(encoding="utf-8")
for literal in (
    'internal enum NexAccentTheme',
    'EmberBrass', 'ArcaneCyan', 'MysticViolet', 'BloodCrimson', 'VerdantEmerald',
    '#080A0D', '#181613', '#594127', '#A97B3F', '#D4AF6A', '#E1BF78',
    'internal sealed class NexGameFrame',
    'internal sealed class NexMajorPanel',
    'internal sealed class NexMinorPanel',
    'internal sealed class NexPanelHeader',
    'internal sealed class NexSectionDivider',
    'internal sealed class NexNavItem',
    'internal sealed class NexIconButton',
    'internal sealed class NexPrimaryButton',
    'internal sealed class NexCommandBar',
    'internal class NexResourceBar',
    'internal sealed class NexXpBar',
    'internal sealed class NexAttributeCell',
    'internal sealed class NexStatusCell',
    'internal sealed class NexEntityGroup',
    'internal sealed class NexQuickAction',
    'internal sealed class NexCountBadge',
    'TryLoadTexture("dark-metal.png")',
    'TryLoadTexture("obsidian-grain.png")',
    'internal static class NexApprovedAssets',
    'avares://NexMUD/Assets/app-icon.png',
    'RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality)',
):
    if literal not in nex_design_text:
        fail(f"prescriptive NexMUD design-system invariant missing: {literal}")
for literal in (
    'World', 'Character', 'Abilities', 'Codex', 'Map', 'Automation', 'Scripting', 'Jev', 'Settings',
    'Search', 'Log', 'History', 'Send', 'Look', 'Scan', 'Where', 'Inventory',
    'Hp', 'Mana', 'Movement', 'Xp', 'Position', 'Combat', 'Target',
    'People', 'Objects', 'Fixtures', 'Corpses',
    'Strength', 'Dexterity', 'Constitution', 'Intelligence', 'Wisdom', 'Charisma',
):
    if f'NexIconKind.{literal}' not in nex_icons_text:
        fail(f"required NexMUD vector icon missing: {literal}")
for literal in (
    'internal sealed class GameplayHudPanel',
    'internal sealed class CharacterHudPanel',
    'internal sealed class RoomContextPanel',
    'NexResourceBar _hp',
    'NexResourceBar _mana',
    'NexResourceBar _move',
    'ColumnDefinitions = new ColumnDefinitions("*,*,*")',
    'AddEntitySection("People"',
    'NexQuickAction',
    'VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto',
):
    if literal not in persistent_gameplay_text:
        fail(f"default World HUD composition invariant missing: {literal}")
for literal in (
    'return new NexGameFrame(root)',
    'Border worldFrame = new()',
    'new NexCommandBar(commandGrid)',
    'NavigationButton("World", ToolView.Context, NexIconKind.World)',
    'NexApprovedAssets.CreateApplicationIcon(34)',
    'Grid.SetRow(_gameplayHudPanel, 1)',
    'ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")',
    'Math.Clamp(_preferredRailWidth, 390, 425)',
    '_workspaceColumn.MinWidth = 380',
    'RowDefinitions = new RowDefinitions("Auto,2,*")',
    'ConfigureTranscriptText(_gameText);',
):
    if literal not in main_window_text:
        fail(f"default World main-shell visual invariant missing: {literal}")
if 'NexMudTheme.Install(this);' not in (ROOT / "src/NexMud.Gui/App.cs").read_text(encoding="utf-8"):
    fail("NexMUD design resources must be installed at application initialization")


scripting_project = ROOT / "src/NexMud.Scripting/NexMud.Scripting.csproj"
if not scripting_project.is_file():
    fail("missing language-neutral scripting project")
else:
    scripting_tree = ET.parse(scripting_project)
    if scripting_tree.findall(".//ProjectReference"):
        fail("NexMud.Scripting must remain independent of application/domain projects")
    if scripting_tree.findall(".//PackageReference"):
        fail("language-neutral scripting contracts must not select an external runtime package yet")

for lower_layer_project in (
    ROOT / "src/NexMud.Contracts/NexMud.Contracts.csproj",
    ROOT / "src/NexMud.Core/NexMud.Core.csproj",
    ROOT / "src/NexMud.Transport/NexMud.Transport.csproj",
    ROOT / "src/NexMud.Adapters/NexMud.Adapters.csproj",
    ROOT / "src/NexMud.Jev/NexMud.Jev.csproj",
):
    lower_tree = ET.parse(lower_layer_project)
    for reference in lower_tree.findall(".//ProjectReference"):
        if "NexMud.Scripting" in (reference.get("Include") or ""):
            fail(f"lower layer may not depend upward on scripting: {lower_layer_project.relative_to(ROOT)}")

behavior_orchestration_files = (
    ROOT / "src/NexMud.Client/Automation/ClientAutomationService.cs",
    ROOT / "src/NexMud.Client/Navigation/AutoMoveService.cs",
    ROOT / "src/NexMud.Client/Runtime/JevDecisionCoordinator.cs",
)
for behavior_file in behavior_orchestration_files:
    behavior_text = behavior_file.read_text(encoding="utf-8")
    for forbidden in ("ActionProcessor _", "Task.Delay(", "DateTimeOffset.UtcNow"):
        if forbidden in behavior_text:
            fail(f"behavior orchestration bypasses shared runtime primitive in {behavior_file.relative_to(ROOT)}: {forbidden}")

script_events_text = (ROOT / "src/NexMud.Scripting/Events/ScriptEvents.cs").read_text(encoding="utf-8")
if "finally\n                {\n                    Close();" not in script_events_text:
    fail("script event subscriptions must unregister automatically when owner execution ends")
script_execution_text = (ROOT / "src/NexMud.Scripting/Execution/ScriptExecutionScope.cs").read_text(encoding="utf-8")
if "event Action<ScriptTaskFault>? TaskFaulted" not in script_execution_text:
    fail("shared execution supervisor must expose owned task faults")

client_script_infrastructure_text = (ROOT / "src/NexMud.Client/Scripting/ClientScriptInfrastructure.cs").read_text(encoding="utf-8")
for literal in (
    "ScriptCommandOrigin.User",
    "WaitForHumanOverrideAsync",
    "WaitForAutomationCommandSlotAsync",
    "Origin = _origin",
    "ModuleId = _moduleId.Value",
    "_state.WaitUntilProcessedAsync(envelope.Sequence",
):
    if literal not in client_script_infrastructure_text:
        fail(f"central command arbitration/provenance invariant missing: {literal}")

for source_file in (ROOT / "src").rglob("*.cs"):
    if source_file == ROOT / "src/NexMud.Client/Scripting/ClientScriptInfrastructure.cs":
        continue
    source_text = source_file.read_text(encoding="utf-8")
    if ".Actions.QueueAsync(" in source_text or "_actions.QueueAsync(" in source_text:
        fail(f"command dispatch bypasses shared command host: {source_file.relative_to(ROOT)}")

if "FullMode = BoundedChannelFullMode.Wait" not in script_events_text:
    fail("script event subscriptions must use bounded backpressure rather than silent event loss")
if "ValidateChildKind" not in script_execution_text:
    fail("script execution owners must constrain child ownership kinds")

script_host_text = (ROOT / "src/NexMud.Scripting/Host/ScriptHostContracts.cs").read_text(encoding="utf-8")
for literal in (
    "IScriptEvents Events",
    "IScriptCommands Commands",
    "IScriptState State",
    "IScriptMapper Mapper",
    "IScriptCodex Codex",
    "IScriptStorage Storage",
    "IScriptScheduler Timers",
    "IScriptUi Ui",
    "IScriptLog Log",
):
    if literal not in script_host_text:
        fail(f"script host capability surface missing: {literal}")

automation_runtime_text = (ROOT / "src/NexMud.Client/Automation/AutomationRuntimeCompiler.cs").read_text(encoding="utf-8")
automation_js_compiler_text = (ROOT / "src/NexMud.Client/Automation/AutomationJavaScriptCompiler.cs").read_text(encoding="utf-8")
automation_ir_text = (ROOT / "src/NexMud.Client/Automation/AutomationIr.cs").read_text(encoding="utf-8")
for literal in (
    "class AutomationRuntimeCompiler",
    "AutomationProgram[] candidatePrograms = BuildPrograms",
    "_platform.JavaScriptRuntime.ReloadAsync",
    "_platform.JavaScriptRuntime.LoadAsync",
    "ScriptCommandOrigin.Automation",
    "ScriptEventTypes.AutomationAliasMatched",
    "ScriptEventTypes.AutomationTextTriggerMatched",
    "ScriptEventTypes.AutomationStateRuleMatched",
):
    if literal not in automation_runtime_text:
        fail(f"automation Jint runtime migration missing: {literal}")
for literal in (
    "interface IAutomationCompiler",
    "class AutomationJavaScriptCompiler",
    "ScriptOwnerKind.AutomationRule",
    "ScriptRuntimeProfile.GeneratedAutomation",
    'import { nex } from "@nexmud/api";',
    "nex.timers.every",
    "nex.timers.after",
    "nex.timers.delay",
    "nex.storage.set",
    "nex.commands.send",
    "automationId: program.id",
):
    if literal not in automation_js_compiler_text:
        fail(f"automation JavaScript compiler invariant missing: {literal}")
for literal in (
    "record AutomationProgram",
    "record AliasAutomationTrigger",
    "record TextAutomationTrigger",
    "record TimerAutomationTrigger",
    "record StateAutomationTrigger",
    "record SendCommandAutomationAction",
    "record DelayAutomationAction",
    "record SetStorageAutomationAction",
):
    if literal not in automation_ir_text:
        fail(f"automation IR invariant missing: {literal}")
legacy_automation_text = (ROOT / "src/NexMud.Client/Automation/ClientAutomationService.cs").read_text(encoding="utf-8")
for forbidden in ("EvaluateTriggersAsync", "EvaluateGameRulesAsync", "RunTimersAsync"):
    if forbidden in legacy_automation_text:
        fail(f"migrated Automation category still executes through legacy runtime: {forbidden}")
if "__nex" in automation_js_compiler_text:
    fail("Automation compiler must emit only public @nexmud/api calls, never private Jint bridge names")
for literal in (
    "ReadScriptStorage = 1 << 13",
    "nex.timers.delay",
    "nex.timers.after",
    "nex.timers.every",
    "nex.timers.cancel",
    "nex.storage.get",
):
    permission_or_sdk_text = (ROOT / "src/NexMud.Scripting/Permissions/ScriptPermissions.cs").read_text(encoding="utf-8") + automation_js_compiler_text + (ROOT / "src/NexMud.Scripting.Jint/Bootstrap/NexMudJavaScriptBootstrap.cs").read_text(encoding="utf-8")
    if literal not in permission_or_sdk_text:
        fail(f"stabilized Automation SDK invariant missing: {literal}")
jint_project = ROOT / "src/NexMud.Scripting.Jint/NexMud.Scripting.Jint.csproj"
if not jint_project.is_file():
    fail("missing Jint scripting adapter project")
else:
    jint_tree = ET.parse(jint_project)
    jint_packages = [(node.get("Include"), node.get("Version")) for node in jint_tree.findall(".//PackageReference")]
    if jint_packages != [("Jint", "4.16.3")]:
        fail(f"Jint adapter must pin exactly Jint 4.16.3: {jint_packages}")
    jint_refs = [node.get("Include") or "" for node in jint_tree.findall(".//ProjectReference")]
    if jint_refs != ["../NexMud.Scripting/NexMud.Scripting.csproj"]:
        fail(f"Jint adapter must depend only on language-neutral scripting contracts: {jint_refs}")

for project in ROOT.glob("src/**/*.csproj"):
    if project == jint_project:
        continue
    tree = ET.parse(project)
    if any((node.get("Include") or "").casefold() == "jint" for node in tree.findall(".//PackageReference")):
        fail(f"Jint package leaked outside adapter project: {project.relative_to(ROOT)}")

jint_source_paths = [
    path
    for path in (ROOT / "src/NexMud.Scripting.Jint").rglob("*.cs")
    if not any(part in {"bin", "obj", "artifacts"} for part in path.parts)
]
jint_source = "\n".join(path.read_text(encoding="utf-8") for path in jint_source_paths)
for forbidden in ("AllowClr(", "EnableModules(", "Process.Start("):
    if forbidden in jint_source:
        fail(f"Jint adapter violates sandbox boundary: {forbidden}")
for path in jint_source_paths:
    jint_path_source = path.read_text(encoding="utf-8")
    for forbidden in ("using System.Reflection", "global using System.Reflection", "System.Reflection."):
        if forbidden in jint_path_source:
            fail(
                f"Jint adapter violates sandbox boundary: {forbidden} "
                f"in {path.relative_to(ROOT)}"
            )
for literal in (
    "class JintEngineFactory",
    "DisableStringCompilation()",
    "Interop.Enabled = false",
    "Interop.AllowSystemReflection = false",
    "AgentCanSuspend = false",
    "Modules.RegisterRequire = false",
    "MaxExecutionStackCount = limits.MaximumExecutionStackDepth",
    "class NexMudTimeSystem",
    "override DateTimeOffset GetUtcNow() => _clock.UtcNow",
    "class JintScriptDispatcher",
    "SingleReader = true",
    "class JintScriptInstance",
    "class JintHostBridge",
    'engine.SetValue("__nexQueryRaw"',
    'engine.SetValue("__nexBeginRaw"',
    'engine.Modules.Add("@nexmud/api"',
    "class JintScriptRuntime",
    "Task ReloadAsync(CompiledScriptPackage package",
    'RuntimeName => "jint-4.16.3"',
):
    if literal not in jint_source:
        fail(f"Jint integration invariant missing: {literal}")

bootstrap_text = (ROOT / "src/NexMud.Scripting.Jint/Bootstrap/NexMudJavaScriptBootstrap.cs").read_text(encoding="utf-8")
for literal in ("'__nexResolve'", "'__nexReject'", "'__nexDispatchEvent'", "'__nexDispatchTimer'", "@nexmud/api", "globalThis, 'nex'", "function deepFreeze", "delay(milliseconds)", "after(milliseconds, handler)", "every(milliseconds, handler)", "has(key)"):
    if literal not in bootstrap_text:
        fail(f"NexMUD JavaScript bootstrap invariant missing: {literal}")
if "const nex = Object.freeze({ events, commands, state, log, timers, storage, mapper });" not in bootstrap_text:
    fail("SDK v1 bootstrap must expose events/commands/state/log/timers/storage plus the Mapper orchestration namespace")
for reserved_namespace in ("codex,", "ui,", "jev,"):
    if reserved_namespace in bootstrap_text.split("const nex = Object.freeze(", 1)[-1].split(");", 1)[0]:
        fail(f"reserved scripting namespace exposed before its architecture slice: {reserved_namespace.rstrip(',')}")

typescript_project = ROOT / "src/NexMud.Scripting.TypeScript/NexMud.Scripting.TypeScript.csproj"
if not typescript_project.is_file():
    fail("missing TypeScript scripting boundary project")
else:
    ts_tree = ET.parse(typescript_project)
    if ts_tree.findall(".//PackageReference"):
        fail("TypeScript boundary must not smuggle in Node/native compiler dependencies")
    ts_refs = [node.get("Include") or "" for node in ts_tree.findall(".//ProjectReference")]
    if ts_refs != ["../NexMud.Scripting/NexMud.Scripting.csproj"]:
        fail(f"TypeScript boundary must depend only on scripting contracts: {ts_refs}")
ts_declarations = (ROOT / "src/NexMud.Scripting.TypeScript/Declarations/NexMudTypeDeclarations.cs").read_text(encoding="utf-8")
for literal in ('ModuleSpecifier = "@nexmud/api"', "declare const nex: NexMudApi", "\"character.vitalsChanged\"", "Promise<NexMudCommandResult>", "NexMudTimersApi", "NexMudStorageApi", "ScriptApiVersion.Current"):
    if literal not in ts_declarations:
        fail(f"TypeScript declaration/API-version invariant missing: {literal}")

solution_text = (ROOT / "NexMud.slnx").read_text(encoding="utf-8")
for project_path in (
    "src/NexMud.Scripting/NexMud.Scripting.csproj",
    "src/NexMud.Scripting.Jint/NexMud.Scripting.Jint.csproj",
    "src/NexMud.Scripting.TypeScript/NexMud.Scripting.TypeScript.csproj",
):
    if project_path not in solution_text:
        fail(f"scripting project must be included in solution: {project_path}")

if "src/NexMud.Scripting/NexMud.Scripting.csproj" not in (ROOT / "NexMud.slnx").read_text(encoding="utf-8"):
    fail("scripting project must be included in solution")
if not (ROOT / "docs/architecture/NexMUD-scripting-platform-architecture.md").is_file():
    fail("missing original scripting platform architecture contract in repository")
if not (ROOT / "docs/architecture/NexMUD-jint-integration-architecture.md").is_file():
    fail("missing NexMUD Jint integration architecture contract in repository")
if not (ROOT / "docs/architecture/NexMUD-scripting-vertical-slice-architecture.md").is_file():
    fail("missing NexMUD scripting vertical-slice architecture contract in repository")
if not (ROOT / "docs/architecture/NexMUD-scripting-sdk-automation-migration-architecture.md").is_file():
    fail("missing NexMUD scripting SDK + Automation migration architecture contract in repository")

vertical_slice_files = (
    ROOT / "src/NexMud.Scripting.TypeScript/Compiler/TypeScriptCompiler.cs",
    ROOT / "src/NexMud.Scripting/Compilation/ScriptSourceMaps.cs",
    ROOT / "scripts/reference/vitals-policy/manifest.json",
    ROOT / "scripts/reference/vitals-policy/main.ts",
)
for required_file in vertical_slice_files:
    if not required_file.is_file():
        fail(f"missing scripting vertical-slice artifact: {required_file.relative_to(ROOT)}")
vertical_slice_source = "\n".join(path.read_text(encoding="utf-8") for path in vertical_slice_files if path.suffix in {".cs", ".ts", ".json"})
for literal in (
    '"character.vitalsChanged"',
    'nex.commands.send("flee")',
    "class TypeScriptCompiler",
    "ScriptSourceMaps",
):
    if literal not in vertical_slice_source:
        fail(f"scripting vertical-slice invariant missing: {literal}")
if "Transport" in (ROOT / "src/NexMud.Scripting.Jint/Host/JintHostBridge.cs").read_text(encoding="utf-8"):
    fail("Jint host bridge must not bypass central command dispatch through Transport")
jint_host_bridge_text = (ROOT / "src/NexMud.Scripting.Jint/Host/JintHostBridge.cs").read_text(encoding="utf-8")
for forbidden_jint_internal in (
    "engine.RunAvailableContinuations(",
    "engine.DrainEventLoopUntilSettled(",
    "engine.DrainEventLoopUntil(",
    "engine.EventLoop",
):
    if forbidden_jint_internal in jint_host_bridge_text:
        fail(f"Jint host bridge must use public Jint APIs only; internal event-loop API referenced: {forbidden_jint_internal}")
if 'engine.Execute("void 0;", "<nexmud-continuation-pump>")' not in jint_host_bridge_text:
    fail("Jint host completions must pump Promise continuations through the public Execute boundary")
if "await invocation.Completion.Task.WaitAsync(cancellationToken)" not in jint_host_bridge_text:
    fail("Jint event subscriptions must preserve FIFO across the full async handler lifecycle")

automation_runtime_text = (ROOT / "src/NexMud.Client/Automation/AutomationRuntimeCompiler.cs").read_text(encoding="utf-8")
for literal in ("EnsureRuntimeRunningAsync", "if (!await EnsureRuntimeRunningAsync(cancellationToken)"):
    if literal not in automation_runtime_text:
        fail(f"Automation alias runtime-health invariant missing: {literal}")
if automation_runtime_text.count("if (!await EnsureRuntimeRunningAsync(cancellationToken)") < 3:
    fail("Automation alias, text-trigger, and state-rule ingress must all require a running shared runtime")

# 0.26.x Mapper orchestration invariants
mapper_architecture = ROOT / "docs/architecture/NexMUD-mapper-orchestration-architecture.md"
if not mapper_architecture.is_file():
    fail("missing Mapper orchestration architecture contract")
for path in (
    ROOT / "src/NexMud.Client/Navigation/MapperQueryService.cs",
    ROOT / "src/NexMud.Client/Navigation/MapperMovementCoordinator.cs",
    ROOT / "src/NexMud.Client/Navigation/MapperNavigationAuthority.cs",
    ROOT / "src/NexMud.Client/Navigation/MapperRouteOrchestration.cs",
):
    if not path.is_file():
        fail(f"missing Mapper orchestration implementation file: {path.relative_to(ROOT)}")
mapper_orchestration_text = (ROOT / "src/NexMud.Client/Navigation/MapperRouteOrchestration.cs").read_text(encoding="utf-8")
mapper_movement_text = (ROOT / "src/NexMud.Client/Navigation/MapperMovementCoordinator.cs").read_text(encoding="utf-8")
mapper_query_text = (ROOT / "src/NexMud.Client/Navigation/MapperQueryService.cs").read_text(encoding="utf-8")
auto_move_text = (ROOT / "src/NexMud.Client/Navigation/AutoMoveService.cs").read_text(encoding="utf-8")
for literal in (
    "class MapperQueryService",
    "FindRouteAsync",
    "ScriptRoutePlan",
):
    if literal not in mapper_query_text:
        fail(f"Mapper domain/query invariant missing: {literal}")
for literal in (
    "class MapperMovementCoordinator",
    "SemaphoreSlim _movementGate",
    "ScriptMovementResult",
    "one edge at a time",
    "ActionProcessor" if False else "ScriptCommandOrigin.Mapper",
):
    if literal not in mapper_movement_text:
        fail(f"Mapper movement-coordinator invariant missing: {literal}")
for forbidden in ("TcpMudTransport", "IMudTransport", "_transport."):
    if forbidden in mapper_movement_text:
        fail("Mapper movement coordinator must not access Transport directly")
for literal in (
    'new("nexmud.mapper.route")',
    '"mapper.route.execute"',
    "nex.events.on",
    "nex.mapper.currentRoom()",
    "nex.mapper.findPath(destination)",
    "nex.mapper.move(step.direction",
    "startupFallback = nex.timers.after(250, startRoute)",
    "MapperRouteControl",
    "RouteBoundScriptMapperHost",
    "const initialPlan =",
    "let seededPlan = initialPlan",
):
    if literal not in mapper_orchestration_text:
        fail(f"Mapper Jint orchestration invariant missing: {literal}")
# Generated Mapper route code and host capability gates must remain permission-closed.
# This catches manifest/host mismatches before runtime (for example a mapper.route.*
# subscription with SubscribeEvents but without MapperRouteObserve).
mapper_generated_capability_requirements = {
    "nex.mapper.currentRoom()": "ScriptCapability.ReadMapper",
    "nex.mapper.findPath(destination)": "ScriptCapability.MapperPathfind",
    "nex.mapper.move(step.direction": "ScriptCapability.MapperMove",
    "nex.timers.": "ScriptCapability.CreateTimers",
    "nex.events.on": "ScriptCapability.SubscribeEvents",
    '"mapper.route.execute"': "ScriptCapability.MapperRouteObserve",
}
permissions_start = mapper_orchestration_text.find("public static ScriptCapability Permissions =>")
permissions_end = mapper_orchestration_text.find("public static CompiledScriptPackage Compile", permissions_start)
if permissions_start < 0 or permissions_end < 0:
    fail("Mapper route compiler permissions block could not be located")
mapper_permissions_text = mapper_orchestration_text[permissions_start:permissions_end]
for sdk_use, capability in mapper_generated_capability_requirements.items():
    if sdk_use in mapper_orchestration_text and capability not in mapper_permissions_text:
        fail(f"Mapper route generated SDK use {sdk_use!r} requires missing manifest capability {capability}")
for literal in (
    "MapperNavigationAuthority",
    "PauseForManualMovementAsync",
    "JavaScriptRuntime.LoadAsync",
    "JavaScriptRuntime.ReloadAsync",
    "JavaScriptRuntime.UnloadAsync",
):
    if literal not in auto_move_text:
        fail(f"Mapper route-control invariant missing: {literal}")
for literal in (
    "MapperRouteScriptCompiler.StartEventType",
    "EventHub.PublishAsync",
    "BindRuntimeIdentity",
    "IsActiveRouteDiagnostic",
    "IsActiveRouteTaskFault",
    "initialPlan = await _mapper.FindPathAsync",
):
    if literal not in auto_move_text:
        fail(f"Mapper route-start reliability invariant missing: {literal}")
load_position = max(
    auto_move_text.find("await _platform.JavaScriptRuntime.LoadAsync"),
    auto_move_text.find("await _platform.JavaScriptRuntime.ReloadAsync"),
)
start_signal_position = auto_move_text.find("MapperRouteScriptCompiler.StartEventType", load_position + 1)
if load_position < 0 or start_signal_position < 0 or start_signal_position <= load_position:
    fail("Mapper route start signal must be published only after the Jint route module load/reload path")
if "nex.timers.after(0, executeRoute)" in mapper_orchestration_text:
    fail("Mapper route startup must not depend on a zero-delay activation timer")
mapper_test_text = (ROOT / "tests/NexMud.Tests/Program.cs").read_text(encoding="utf-8")
if "MapperRouteExplicitStartSignalBeginsOrchestration" not in mapper_test_text:
    fail("Mapper route startup regression test is missing")
if "MapperRouteResumesAfterDelayedPathfinding" not in mapper_test_text:
    fail("Mapper delayed-I/O Promise continuation regression test is missing")
if "CompiledAutomationAliasResumesAfterDelayedHostCompletion" not in mapper_test_text:
    fail("Automation delayed-I/O Promise continuation regression test is missing")

# Cross-project scripting runtime symbols must import their defining namespace.
# This catches compile failures which structural string checks otherwise miss when a migration
# introduces a runtime enum/record into Client, GUI, Automation, Mapper, or future Jev code.
runtime_namespace = "NexMud.Scripting.Runtime"
runtime_symbols = (
    "ScriptModuleId",
    "ScriptOwnerKind",
    "ScriptOwner",
    "IScriptContext",
    "IScriptModule",
    "ScriptStatus",
    "ScriptInvocationStatus",
    "ScriptInvocationSnapshot",
    "ScriptModuleSnapshot",
    "IScriptRuntime",
    "IJavaScriptRuntime",
    "ManagedScriptRuntime",
    "ScriptRuntimeProfile",
    "ScriptResourceLimits",
    "ScriptRuntimePolicy",
)
for cs_file in ROOT.rglob("*.cs"):
    text = cs_file.read_text(encoding="utf-8")
    if f"namespace {runtime_namespace};" in text:
        continue
    for symbol in runtime_symbols:
        if symbol not in text:
            continue
        if f"using {runtime_namespace};" in text or f"{runtime_namespace}.{symbol}" in text:
            continue
        fail(
            f"{cs_file.relative_to(ROOT)} references {symbol} without importing {runtime_namespace}"
        )
for literal in (
    "currentRoom() { return begin(\'mapper.currentRoom\'",
    "findPath(destination, options = {})",
    "move(direction, options = {})",
):
    if literal not in bootstrap_text:
        fail(f"nex.mapper bootstrap invariant missing: {literal}")
script_host_contracts = (ROOT / "src/NexMud.Scripting/Host/ScriptHostContracts.cs").read_text(encoding="utf-8")
for literal in (
    "Task<ScriptRoomSnapshot?> CurrentRoomAsync",
    "Task<ScriptRoutePlan?> FindPathAsync",
    "Task<ScriptMovementResult> MoveAsync",
):
    if literal not in script_host_contracts:
        fail(f"Mapper SDK host invariant missing: {literal}")
for forbidden in ("MoveToAsync", '"mapper.moveTo"'):
    if forbidden in source:
        fail(f"legacy Mapper scripting execution path remains: {forbidden}")

for required_file in (
    ROOT / "src/NexMud.Gui/Assets/app-icon.icns",
    ROOT / "src/NexMud.Gui/Assets/app-icon.ico",
    ROOT / "scripts/run-macos-app.sh",
    ROOT / "src/NexMud.Gui/CommandGesture.cs",
    ROOT / "src/NexMud.Gui/CrashDiagnostics.cs",
):
    if not required_file.is_file():
        fail(f"missing application identity/convenience file: {required_file.relative_to(ROOT)}")

ornaments_dir = ROOT / "src/NexMud.Gui/Assets/Ornaments"
if ornaments_dir.exists():
    fail("regression-recovery release must not ship the rejected generated gameplay ornament directory")

for forbidden_asset_reference in (
    "NexMudAssets",
    "Assets/Ornaments",
    "character-crest.png",
    "section-divider.png",
    "navigation-active-ornament.png",
    "jev-sigil.png",
):
    if forbidden_asset_reference in nex_design_text or forbidden_asset_reference in persistent_gameplay_text or forbidden_asset_reference in main_window_text:
        fail(f"rejected generated gameplay asset reference remains: {forbidden_asset_reference}")

readme = (ROOT / "README.md").read_text(encoding="utf-8")
if not readme.startswith("# NexMUD Client v0.30.0"):
    fail("README current version must be NexMUD 0.30.0")
if '<Version>0.30.0</Version>' not in (ROOT / "src/NexMud.Gui/NexMud.Gui.csproj").read_text(encoding="utf-8"):
    fail("GUI SemVer must be 0.30.0 for the current NexMUD release")
macos_build_script = (ROOT / "scripts/build-macos-app.sh").read_text(encoding="utf-8")
if '<string>0.30.0</string>' not in macos_build_script:
    fail("macOS CFBundleShortVersionString must be 0.30.0")
if '<string>30000</string>' not in macos_build_script:
    fail("macOS CFBundleVersion must be 30000 for NexMUD 0.30.0")
item_inspection_text = (ROOT / "src/NexMud.Gui/ItemInspectionPopover.cs").read_text(encoding="utf-8")
if "using Avalonia;" not in item_inspection_text:
    fail("ItemInspectionPopover must import Avalonia for Thickness/CornerRadius")

if "suppressed from game output" in readme:
    fail("README still documents destructive prompt suppression")
if "semantic extraction cannot suppress it" not in readme:
    fail("README must document the append-only server-output invariant")


# 0.23.0 alias expansion must feed separators introduced by aliases back through batching.
alias_input_text = (ROOT / "src/NexMud.Client/Commands/CommandInputExpander.cs").read_text(encoding="utf-8")
if "CommandAliasExpander.TryExpand" not in alias_input_text or "CommandBatchSplitter.Split(command, separator)" not in alias_input_text:
    fail("alias expansion must occur before final command-batch splitting")
if "SplitPreservingEscapes" not in alias_input_text:
    fail("alias command planning must preserve escaped separators until final expansion")
if "_runtime.Interaction.Input.SubmitAsync" not in main_window_text:
    fail("Avalonia command submission must use the shared interaction input pipeline")
tui_main_window_text = (ROOT / "src/NexMud.Tui/Views/MainWindow.cs").read_text(encoding="utf-8")
if "_runtime.Interaction.Input.SubmitAsync" not in tui_main_window_text:
    fail("TUI command submission must use the shared interaction input pipeline")
if "_runtime.Interaction.World.Appended += HandleWorldBufferAppended" not in tui_main_window_text:
    fail("TUI world output must project the shared logical WorldBuffer")

# 0.19.x gameplay shell compile-safety
gameplay_shell_text = (ROOT / "src/NexMud.Gui/GameplayShellViewModels.cs").read_text(encoding="utf-8")
if "using NexMud.Contracts.Events;" not in gameplay_shell_text:
    fail("gameplay shell must import ConnectionStatus from NexMud.Contracts.Events")
if "out AttributeScore? score" not in gameplay_shell_text or "score is null" not in gameplay_shell_text:
    fail("gameplay shell AttributeScore lookup must be nullable-safe under warnings-as-errors")

# 0.19.1 regression-recovery invariants retained in 0.20.x
command_input_policy_text = (ROOT / "src/NexMud.Client/Presentation/CommandInputPolicy.cs").read_text(encoding="utf-8")
avendar_adapter_text = (ROOT / "src/NexMud.Adapters/Avendar/AvendarGameAdapter.cs").read_text(encoding="utf-8")
room_state_text = (ROOT / "src/NexMud.Contracts/State/StateModels.cs").read_text(encoding="utf-8")
room_event_text = (ROOT / "src/NexMud.Contracts/Events/MudEvents.cs").read_text(encoding="utf-8")
room_parser_text = (ROOT / "src/NexMud.Adapters/Avendar/AvendarRoomContentsParser.cs").read_text(encoding="utf-8")
test_program_text = (ROOT / "tests/NexMud.Tests/Program.cs").read_text(encoding="utf-8")
for literal in (
    "SessionInputMode.LoginName => new(",
    'Placeholder: "Character name"',
    "SessionInputMode.LoginPassword => new(",
    'Placeholder: "Password"',
    "AllowHistory: false",
    "ClearAfterSubmit: true",
    "CommandInputTransitionPresentation Transition",
    "ShouldEchoToTranscript",
):
    if literal not in command_input_policy_text:
        fail(f"credential input-state invariant missing: {literal}")
for literal in (
    "ObservePartialInputPromptAsync",
    'protocol.Protocol.Equals("ECHO"',
    "TryPublishCompleteScoreCoreAsync",
    "PublishInputModeAsync(SessionInputMode.Normal",
):
    if literal not in avendar_adapter_text:
        fail(f"Avendar input/response-state recovery invariant missing: {literal}")
if "RecentObservations" not in room_state_text or "RecentObservations" not in room_event_text:
    fail("canonical room/event state must retain recent room observations")
if "RecentObservations = new ReadOnlyCollection<string>" not in room_parser_text:
    fail("Avendar room parser must project dynamic observations into canonical room events")
if "state.Room.RecentObservations" not in gameplay_shell_text:
    fail("room gameplay projection must consume canonical recent observations")
if "AvendarScoreParser" in gameplay_shell_text or "AvendarScoreParser" in persistent_gameplay_text or "AvendarScoreParser" in main_window_text:
    fail("GUI must not parse score transcript text directly")
for literal in (
    "_gameplayHudPanel.Update(viewModel.Character);",
    "_characterHudPanel.Update(viewModel.Character);",
):
    if literal not in main_window_text:
        fail(f"HUD/Character shared canonical projection invariant missing: {literal}")
for literal in (
    "AvendarAdapterFollowsExplicitLoginInputState",
    "AvendarScoreCorePublishesImmediately",
    "RandolphScoreReducesToCanonicalCharacterState",
    "GoldenRoomStatePreservesCanonicalContext",
    "GameplayProjectionsUseCanonicalState",
):
    if literal not in test_program_text:
        fail(f"regression coverage missing: {literal}")

# 0.23.0 World information-architecture and terminal-fidelity invariants
for literal in (
    'NavigationButton("World", ToolView.Context, NexIconKind.World)',
    'NavigationButton("Character", ToolView.Character, NexIconKind.Character)',
    'bool active = normalized == NormalizeToolView(_activeTool);',
    'button.SetActive(active);',
    'UpdateNavigationDensity()',
    'RenderConnectionAction(connected: false, _runtime.ActiveConnectionProfile.Host, _runtime.ActiveConnectionProfile.Port);',
    'RenderConnectionAction(connected, connectionHost, connectionPort);',
    '_gameplayHudPanel.Update(viewModel.Character);',
    '_characterHudPanel.Update(viewModel.Character);',
):
    if literal not in main_window_text:
        fail(f"0.21.0 main-shell invariant missing: {literal}")
if 'bool active = world ||' in main_window_text or 'button.SetActive(active, world);' in main_window_text:
    fail("World navigation must not receive special active-state treatment while another workspace is active")
for forbidden in (
    '_connectionLabel',
    '_connectionIcon',
    '_characterHudPanel.Expand()',
):
    if forbidden in main_window_text:
        fail(f"obsolete 0.21.0 shell mechanism remains: {forbidden}")
for literal in (
    'private void RenderConnectionAction(bool connected, string host, int port)',
    'string address = $"{host}:{port}";',
    '? $"Connected to {address}. Click to disconnect."',
    ': $"Disconnected. Click to connect to {address}."',
):
    if literal not in main_window_text:
        fail(f"combined connection-control invariant missing: {literal}")

for forbidden in (
    '_detailScroll',
    '_disclosure',
    'SetExpanded(',
    'public void Expand()',
):
    if forbidden in persistent_gameplay_text:
        fail(f"Character disclosure/scroll regression returned: {forbidden}")
for literal in (
    '_observedDetails.IsVisible = identified;',
    'RenderLoadout(viewModel.Equipment);',
    'ItemInspectionPopover.Build(_inspectionResolver(item.Item, item.Slot))',
    'AddEntitySection("Fixtures", NexIconKind.Fixtures, viewModel.Fixtures);',
    'No durable room memory yet',
    'BorderThickness = new Thickness(0)',
):
    if literal not in persistent_gameplay_text:
        fail(f"0.21.0 persistent gameplay invariant missing: {literal}")
for literal in (
    'string Items,',
    'string Weight,',
    'string EquipmentSummary,',
    'IReadOnlyList<CharacterEquipmentViewModel> Equipment,',
    'state.Room.Name ?? metadata?.Label ?? "Room not observed"',
):
    if literal not in gameplay_shell_text:
        fail(f"gameplay projection invariant missing: {literal}")

# 0.23.0 right-rail + Character/Inventory workspace
for literal in (
    'CharacterInventoryWorkspace _characterWorkspace',
    '_workspaceHost.Content = _characterWorkspace',
    'case ToolView.Character:',
    '_characterWorkspace.Update(_snapshot, viewModel.Character);',
    'Concat(snapshot.Character.CarriedItems',
    'ResolveItemInspection(string itemName, string? slot)',
):
    if literal not in main_window_text:
        fail(f"0.23.0 Character workspace integration missing: {literal}")
for literal in (
    'internal sealed class CharacterInventoryWorkspace',
    'PaperDollSlots',
    'Search carried items',
    'InventoryCompleteness',
    'ItemInspectionPopover.Build',
    'id {inspectedItem}',
):
    if literal not in character_workspace_text:
        fail(f"0.23.0 Character/Inventory workspace invariant missing: {literal}")
for literal in (
    'internal static class ItemInspectionPopover',
    'ItemInspectionData',
    'CodexKnowledge',
    'LiveIdentification',
):
    if literal not in item_popover_text:
        fail(f"shared item inspection invariant missing: {literal}")

# 0.24.1 typography + item-inspection correction pass
if not (ROOT / "docs/design/NexMUD-typography-item-inspection-correction-pass.md").is_file():
    fail("missing typography + item-inspection correction design contract")
for literal in (
    'internal static class NexTypography',
    'app.Resources["Typography.Display"]',
    'app.Resources["Typography.HudStrong"]',
    'public const double CompactData = 12.5;',
    'public const double Monospace = 13;',
):
    if literal not in nex_design_text:
        fail(f"0.24.1 semantic typography invariant missing: {literal}")
for literal in (
    'private string? _hoveredItem;',
    'CurrentInspectedItem => _hoveredItem ?? _selectedItem',
    'PointerEntered +=',
    'PointerExited +=',
    'MinWidth = 280',
    'NexTypography.Metadata',
    'NexTypography.CompactData',
    'NexTypography.Body',
):
    if literal not in character_workspace_text:
        fail(f"0.24.1 Character typography/inspection behavior missing: {literal}")
for literal in (
    'PlayerFacingLabels',
    'BuildPrimaryStatsGrid',
    'BuildModifierRow',
    'BuildFlagChips',
    'Observed this session • Identified',
    'LowerIsBeneficial',
    'HigherIsBeneficial',
):
    if literal not in item_popover_text:
        fail(f"0.24.1 item-inspection presentation invariant missing: {literal}")
for literal in (
    '_level.FontSize = NexTypography.HudStrong;',
    '_next.FontSize = NexTypography.Hud;',
    'value.FontSize = NexTypography.Hud;',
):
    if literal not in persistent_gameplay_text:
        fail(f"0.24.1 gameplay HUD typography invariant missing: {literal}")
if '_value.FontSize = height >= 18 ? NexTypography.HudStrong : NexTypography.Metadata;' not in nex_design_text:
    fail("0.24.1 resource-bar value typography invariant missing")
for source_name, source_text in (
    ("CharacterInventoryWorkspace", character_workspace_text),
    ("ItemInspectionPopover", item_popover_text),
):
    for line in source_text.splitlines():
        stripped = line.strip()
        if stripped.startswith("FontSize = ") and stripped[len("FontSize = "):].lstrip()[:1].isdigit():
            fail(f"{source_name} uses a local numeric FontSize instead of NexTypography: {stripped}")
# 0.24.2 workspace typography correction
workspace_typography_sources = {
    "ScriptingWorkspace": scripting_workspace_text,
    "AutomationWorkspace": automation_workspace_text,
    "MapperWorkspace": mapper_workspace_text,
    "CodexWorkspace": codex_workspace_text,
    "PersistentGameplayPanels": persistent_gameplay_text,
    "SettingsWorkspace": settings_workspace_text,
}
for source_name, source_text in workspace_typography_sources.items():
    if "NexTypography." not in source_text:
        fail(f"0.24.2 {source_name} must consume centralized NexTypography roles")
    for line in source_text.splitlines():
        if "FontSize = " not in line:
            continue
        tail = line.split("FontSize = ", 1)[1].lstrip()
        if tail[:1].isdigit():
            fail(f"0.24.2 {source_name} retains local numeric typography: {line.strip()}")
        if tail.startswith("UiTheme.Text"):
            fail(f"0.24.2 {source_name} retains legacy UiTheme text sizing: {line.strip()}")

for source_name, source_text in (
    ("ScriptingWorkspace", scripting_workspace_text),
    ("AutomationWorkspace", automation_workspace_text),
    ("MapperWorkspace", mapper_workspace_text),
    ("CodexWorkspace", codex_workspace_text),
):
    if "FontSize = NexTypography.Body;" not in source_text:
        fail(f"0.24.2 {source_name} must establish a readable inherited body baseline")

if 'ColumnDefinitions = new ColumnDefinitions("210,*")' not in settings_workspace_text:
    fail("0.24.2 Settings navigation width must accommodate readable typography")
if 'FontSize = NexTypography.Body;' not in settings_workspace_text:
    fail("0.24.2 Settings must establish a readable inherited body baseline")
if 'ColumnDefinitions = new ColumnDefinitions("300,*")' not in codex_workspace_text:
    fail("0.24.2 Codex browser list width must accommodate readable body text")
for literal in (
    "private const double NodeWidth = 164;",
    "private const double NodeHeight = 62;",
    "NexTypography.Metadata, primary",
    "NexTypography.Metadata, secondary",
    "NexTypography.Metadata, brush",
):
    if literal not in mapper_workspace_text:
        fail(f"0.24.2 mapper typography/geometry invariant missing: {literal}")

workspace_start = main_window_text.index("    private Control BuildContextPanel(StateSnapshot state)")
workspace_end = main_window_text.index("    private Task EvaluateCombatAsync()", workspace_start)
main_workspace_typography_text = main_window_text[workspace_start:workspace_end]
for line in main_workspace_typography_text.splitlines():
    if "FontSize = " not in line:
        continue
    tail = line.split("FontSize = ", 1)[1].lstrip()
    if tail[:1].isdigit():
        fail(f"0.24.2 World/Abilities/Jev retains local numeric typography: {line.strip()}")
    if tail.startswith("UiTheme.Text"):
        fail(f"0.24.2 World/Abilities/Jev retains legacy UiTheme text sizing: {line.strip()}")
for literal in (
    "FontSize = NexTypography.Display",
    "FontSize = NexTypography.SectionTitle",
    "FontSize = NexTypography.Body",
    "FontSize = NexTypography.BodyStrong",
    "FontSize = NexTypography.Metadata",
):
    if literal not in main_workspace_typography_text:
        fail(f"0.24.2 World/Abilities/Jev semantic typography role missing: {literal}")

for literal in (
    'InventorySnapshotObserved',
    'CarriedItems',
    'InventoryCompleteness',
):
    if literal not in source:
        fail(f"canonical carried-inventory state missing: {literal}")
if 'ActionButton("Scan", NexIconKind.Scan, "scan")' in persistent_gameplay_text:
    fail("right rail must not send invalid bare scan command")
for literal in (
    'scan {direction}',
    'ContextMenu',
    'ActionButton("Look", NexIconKind.Look, "look")',
    'ActionButton("Where", NexIconKind.Where, "where")',
    'ActionButton("Inv", NexIconKind.Inventory, "inventory")',
):
    if literal not in persistent_gameplay_text:
        fail(f"directional-scan/context-action invariant missing: {literal}")
for literal in (
    'AvendarInventoryParserCapturesCarriedItems',
    'InventorySnapshotPopulatesCanonicalItems',
):
    if literal not in test_program_text:
        fail(f"0.23.0 inventory regression coverage missing: {literal}")

# Terminal typography and application-wide density remain centralized and regression-checked.
client_settings_text = (ROOT / "src/NexMud.Client/Settings/ClientSettings.cs").read_text(encoding="utf-8")
client_settings_store_text = (ROOT / "src/NexMud.Client/Settings/ClientSettingsStore.cs").read_text(encoding="utf-8")
runtime_text = (ROOT / "src/NexMud.Client/Runtime/NexMudRuntime.cs").read_text(encoding="utf-8")
line_ending_text = (ROOT / "src/NexMud.Gui/TranscriptLineEndingNormalizer.cs").read_text(encoding="utf-8")
for literal in (
    'public static readonly FontFamily Terminal = ResolveTerminalFont();',
    'if (OperatingSystem.IsMacOS()) return new FontFamily("Menlo");',
    'if (OperatingSystem.IsWindows()) return new FontFamily("Consolas");',
    'return new FontFamily("DejaVu Sans Mono");',
    'public const double DefaultFontSize = 14;',
    'public const double LineHeightRatio = 8d / 7d;',
    'text.LineHeight = Math.Round(normalized * normalizedRatio, 2);',
    'text.LineSpacing = 0;',
    'text.LetterSpacing = 0;',
    'text.Margin = new Thickness(0);',
    '_gameScroll.Content = TranscriptTextHost(_gameText);',
    '_liveScroll.Content = TranscriptTextHost(_liveText);',
    'FontFamily = NexMudTheme.Terminal,',
    '_gameScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;',
    '_liveScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;',
):
    if literal not in (nex_design_text + main_window_text):
        fail(f"terminal typography invariant missing: {literal}")
for literal in (
    'app.Resources["Transcript.FontSize"]',
    'app.Resources["Transcript.LineHeight"]',
    'app.Resources["Typography.Display"]',
    'app.Resources["Typography.SectionTitle"]',
    'app.Resources["Typography.Body"]',
    'app.Resources["Typography.BodyStrong"]',
    'app.Resources["Typography.Metadata"]',
    'app.Resources["Typography.Compact"]',
    'app.Resources["Typography.Mono"]',
    'app.Resources["Typography.Hud"]',
    'app.Resources["Typography.HudStrong"]',
    'app.Resources["Spacing.Micro"]',
    'app.Resources["Spacing.Tight"]',
    'app.Resources["Spacing.Normal"]',
    'app.Resources["Spacing.Section"]',
):
    if literal not in nex_design_text:
        fail(f"central typography/spacing resource missing: {literal}")
if "_presentationLineEndings.Process(displayRaw)" not in interaction_output_text:
    fail("display line-ending normalization must remain in the presentation-only output path")
for literal in (
    'private bool _pendingCarriageReturn;',
    'private bool _lastOutputWasLineFeed;',
    "if (text[index + 1] == '\\n')",
    "output.Append('\\n');",
):
    if literal not in line_ending_text:
        fail(f"display line-ending normalization invariant missing: {literal}")
if 'Math.Max(14, _runtime.Settings.TranscriptFontSize)' in main_window_text:
    fail("hidden 14-point transcript floor must not return")
for literal in (
    'ConfigureTranscriptText(_gameText);',
    'ConfigureTranscriptText(_liveText);',
    'double lineHeight = NexTranscriptTypography.LineHeight(fontSize);',
):
    if literal not in main_window_text:
        fail(f"runtime transcript metric synchronization invariant missing: {literal}")
if 'Math.Floor(height / lineHeight)' not in main_window_text:
    fail("NAWS row sizing must use the same terminal transcript line height")
if 'fontSize * 1.35' in main_window_text:
    fail("legacy oversized transcript row-height estimate must not return")
if 'double TranscriptFontSize = 14' not in client_settings_text:
    fail("new NexMUD profiles must default transcript typography to 14")
if 'TranscriptSize = Math.Clamp(appearance.TranscriptSize, 8, 24)' not in client_settings_store_text:
    fail("persisted transcript font sizes must normalize to 8-24")
if 'TranscriptFontSize = appearance.TranscriptSize' not in runtime_text:
    fail("runtime transcript font size saving must synchronize with Appearance")
if '_transcriptSize.ItemsSource = new double[] { 8, 9, 10, 11' not in settings_workspace_text:
    fail("settings must retain the supported compact transcript size range")
for literal in (
    'ClientSettingsAllowCompactTranscriptSizes',
    'Assert.Equal(14d, ClientSettings.Default.TranscriptFontSize);',
    'TranscriptTypographyUsesFixedTerminalDensity',
    'Assert.Equal(320d, NexTranscriptTypography.HeightForRows(20, 14));',
    'OutputPreservesTerminalLineStructure',
    'TranscriptDisplayNormalizesCrLf',
    'normalizer.Process("one\\r\\ntwo\\r\\n")',
    'normalizer.Process("three\\n\\rfour\\n\\r")',
    'runtime.ProcessServerOutput(" continued\\n\\rline two\\n\\r"',
):
    if literal not in test_program_text:
        fail(f"terminal regression coverage missing: {literal}")

# 0.21.0 shell density: one World frame, edge-to-edge command surface, no nested Character frame.
for literal in (
    'Grid body = new() { Margin = new Thickness(2, 2, 2, 2) };',
    '_workspaceSplitterColumn.Width = new GridLength(2);',
    'Padding = new Thickness(0)',
):
    if literal not in main_window_text:
        fail(f"0.21.0 shell density invariant missing: {literal}")
for literal in (
    'HorizontalContentAlignment = HorizontalAlignment.Stretch;',
    'HorizontalAlignment = HorizontalAlignment.Stretch;',
    'Padding = new Thickness(5, 3)',
):
    if literal not in nex_design_text:
        fail(f"0.21.0 command-bar stretch invariant missing: {literal}")


# 0.22.0 gameplay HUD density and semantic-state invariants
hud_spec = ROOT / "docs/design/NexMUD-gameplay-hud-strip-redesign.md"
if not hud_spec.is_file():
    fail("missing gameplay HUD redesign design contract")
for literal in (
    'private readonly TextBlock _next = new();',
    'NexResourceBar _hp = new(NexMudTheme.Hp, 20)',
    'NexResourceBar _mana = new(NexMudTheme.Mana, 20)',
    'NexResourceBar _move = new(NexMudTheme.Movement, 20)',
    '_level.Text = $"LVL {viewModel.Level}";',
    '_next.Text = $"NEXT {viewModel.ExperienceRemainingText}";',
    'ColumnDefinitions = new ColumnDefinitions("Auto,*,*,*,Auto")',
    'MinHeight = 48',
    'MaxHeight = 54',
    'Padding = new Thickness(12, 4)',
    'ColumnSpacing = 5',
    'BuildTacticalCluster()',
    'RowDefinitions = new RowDefinitions("Auto,Auto")',
    '_combat.Text = viewModel.CombatActive ? "ENGAGED" : "Clear";',
    '_target.Text = hasTarget ? viewModel.Target : "No target";',
    'NexMudTheme.Target',
    'if (!enabled) return "Off";',
    'return "Acting";',
    'bar.Set(vital.CurrentValue, vital.MaximumValue, vital.Display);',
):
    if literal not in persistent_gameplay_text:
        fail(f"0.22.0 gameplay HUD invariant missing: {literal}")
for forbidden in (
    '_xpText',
    '_xpRemaining',
    'NexXpBar _xp',
    'viewModel.ExperienceText',
    'Exact current-level XP boundaries are unknown',
    'Status("Position"',
    'Status("Combat"',
    'Status("Target"',
    'Status("Jev"',
):
    if forbidden in persistent_gameplay_text:
        fail(f"obsolete pre-0.22 HUD mechanism remains: {forbidden}")
for literal in (
    'private readonly TextBlock _value = new();',
    'public void Set(double current, double maximum, string? valueText = null)',
    '_value.Text = valueText ?? string.Empty;',
    'public static readonly IBrush Target = Brush("#9B7DE3");',
):
    if literal not in nex_design_text:
        fail(f"0.22.0 resource-bar/design token invariant missing: {literal}")
if 'string remainingText = state.Character.ExperienceToLevel is long remaining' not in gameplay_shell_text or '? $"{remaining:N0} XP"' not in gameplay_shell_text:
    fail("HUD progression projection must expose XP-to-next-level without total-XP formatting")
if 'Assert.Equal("1,727 XP", character.ExperienceRemainingText);' not in test_program_text:
    fail("gameplay projection regression must assert compact XP-to-next-level text")

# 0.20.x single-window interaction architecture
for forbidden in (
    "AuxiliaryWindowHost",
    "settings.Show(this);",
    "separate non-modal windows",
):
    if forbidden in main_window_text or forbidden in settings_workspace_text:
        fail(f"rejected multi-window UI model returned: {forbidden}")
if "public sealed class SettingsWorkspace : UserControl" not in settings_workspace_text:
    fail("Settings must remain an in-app UserControl workspace")
for literal in (
    "new GridLength(50, GridUnitType.Star)",
    "new GridLength(53, GridUnitType.Star)",
    "new GridLength(56, GridUnitType.Star)",
    "new GridLength(42, GridUnitType.Star)",
):
    if literal not in main_window_text:
        fail(f"required integrated workspace ratio missing: {literal}")
if 'RowDefinitions = new RowDefinitions("*,Auto,Auto")' not in main_window_text:
    fail("World surface must keep transcript, gameplay HUD, and command input mounted together")

# 0.28.0 gameplay semantic source/reconciliation architecture
semantic_spec = ROOT / "docs/architecture/NexMUD-gameplay-semantics-event-normalization-state-reconciliation-architecture.md"
if not semantic_spec.is_file():
    fail("missing gameplay semantics architecture contract")
observation_factory_text = (ROOT / "src/NexMud.Adapters/Avendar/AvendarObservationFactory.cs").read_text(encoding="utf-8")
avendar_adapter_text = (ROOT / "src/NexMud.Adapters/Avendar/AvendarGameAdapter.cs").read_text(encoding="utf-8")
semantic_parser_text = (ROOT / "src/NexMud.Adapters/Avendar/AvendarSemanticParser.cs").read_text(encoding="utf-8")
command_journal_text = (ROOT / "src/NexMud.Client/Commands/OutboundCommandJournal.cs").read_text(encoding="utf-8")
for literal in (
    "public sealed record GameObservation(",
    "public sealed record CharacterPromptSnapshot(",
    "public sealed record MovementObservation(",
    "public sealed record ScanObservation(",
):
    if literal not in (ROOT / "src/NexMud.Contracts/Gameplay/GameplayModels.cs").read_text(encoding="utf-8"):
        fail(f"gameplay semantic contract missing: {literal}")
for literal in (
    "Interlocked.Increment(ref _sequence)",
    "public GameObservation CreateEvidence(",
    "new GameObservationReceived(observation)",
    "ReplayObservationAsync(",
):
    if literal not in (observation_factory_text + avendar_adapter_text):
        fail(f"game observation invariant missing: {literal}")
if "case GameObservationReceived observation when" not in interaction_output_text:
    fail("display transformations must branch from immutable GameObservation evidence")
if "case GameTextReceived game:" in interaction_output_text:
    fail("display transformations must not use the compatibility raw-text event as canonical source")
for literal in (
    "GameCommandQueueCleared",
    "MovementResult.CombatRestricted",
    "MovementCause.Teleport",
    "CombatTargetConditionObserved",
):
    if literal not in semantic_parser_text:
        fail(f"Avendar semantic parser invariant missing: {literal}")
for literal in (
    "OutboundCommandState.Dispatched",
    "OutboundCommandState.TransportWritten",
    "OutboundCommandState.ServerQueueCleared",
):
    if literal not in command_journal_text:
        fail(f"outbound command journal invariant missing: {literal}")
for literal in (
    "AvendarReplayInfersLegacyStructure",
    "OpaqueSpecialMovementPreservesUnknownDestination",
    "CommandJournalPreservesRepeatedCommandBursts",
    "SemanticCorpusFixturesCoverExpertLogs",
):
    if literal not in test_program_text:
        fail(f"gameplay semantic regression coverage missing: {literal}")

# 0.30.0 product-ownership architecture
product_settings_text = (ROOT / "src/NexMud.Client/Settings/ProductSettings.cs").read_text(encoding="utf-8")
connection_editor_text = (ROOT / "src/NexMud.Gui/ConnectionProfilesEditor.cs").read_text(encoding="utf-8")
output_rules_editor_text = (ROOT / "src/NexMud.Gui/OutputRulesEditor.cs").read_text(encoding="utf-8")
mapper_preferences_text = (ROOT / "src/NexMud.Gui/MapperPreferencesEditor.cs").read_text(encoding="utf-8")
for literal in (
    "General,",
    "Appearance,",
    "Transcript,",
    "Input,",
    "Connections,",
    "DataLogging,",
    "Advanced",
):
    if literal not in settings_workspace_text:
        fail(f"0.30.0 Settings ownership page missing: {literal}")
for forbidden in (
    "SettingsPage.Keybindings",
    "SettingsPage.Automation",
    "SettingsPage.Rules",
    "SettingsPage.Protocols",
    "SettingsPage.Mapper",
    "SettingsPage.Jev",
):
    if forbidden in settings_workspace_text:
        fail(f"0.30.0 domain behavior leaked into global Settings: {forbidden}")
for literal in (
    "record ConnectionProfile(",
    "enum ProtocolPolicy",
    "ProtocolPolicy.Auto",
    "record AppearancePreferences(",
    "record GeneralPreferences(",
):
    if literal not in product_settings_text:
        fail(f"0.30.0 product settings contract missing: {literal}")
for literal in (
    'Text = "PROFILE LIBRARY"',
    'SectionTitle("Advanced Protocols"',
    'ProtocolRow("GMCP"',
    'ProtocolRow("CHARSET"',
    'QuietButton("Duplicate")',
):
    if literal not in connection_editor_text:
        fail(f"0.30.0 connection profile UX missing: {literal}")
for literal in (
    'Section("Highlights"',
    'Section("Transformations"',
    "presentation only",
):
    if literal not in output_rules_editor_text:
        fail(f"0.30.0 transcript Output Rules ownership missing: {literal}")
for literal in (
    '"Aliases", "Keybindings", "Triggers", "State Rules", "Workflows", "Timers", "Script-backed"',
    'BuildKeybindingEditor',
    'BuildStateRuleEditor',
    'BuildTemplates',
    'Disabled group',
):
    if literal not in automation_workspace_text:
        fail(f"0.30.0 Automation ownership missing: {literal}")
for literal in (
    'Text = "MAP PREFERENCES"',
    'Section("Avoidance"',
    'Section("Auto-move"',
):
    if literal not in mapper_preferences_text:
        fail(f"0.30.0 Map preferences ownership missing: {literal}")
for literal in (
    'JevSection.Authority',
    'JevSection.RecentDecisions',
    'JevSection.ProviderSettings',
    'Text = "Provider Settings"',
):
    if literal not in main_window_text:
        fail(f"0.30.0 Jev ownership surface missing: {literal}")
if 'new AutomationKeybindingInvoked(' not in main_window_text:
    fail("0.30.0 keybinding provenance must enter Automation activity")
if 'ActiveConnectionProfile' not in main_window_text or 'ActiveConnectionProfile' not in runtime_text:
    fail("0.30.0 runtime/main shell must use active connection profiles")
if 'SaveSettingsWorkspaceAsync(' not in runtime_text or 'SaveSettingsWorkspaceAsync(' not in settings_workspace_text:
    fail("0.30.0 Settings must persist global preferences and connection profiles atomically")
if product_settings_text.count("[JsonIgnore]") < 1 or client_settings_text.count("[JsonIgnore]") < 2:
    fail("0.30.0 computed connection-profile projections must not serialize into settings")
if 'Enum.GetValues<OutputRuleActionKind>()' not in output_rules_editor_text:
    fail("0.30.0 Output Rules editor must preserve the complete transformation action surface")
if 'MoveHighlight(draft, -1)' not in output_rules_editor_text or 'MoveHighlight(draft, 1)' not in output_rules_editor_text:
    fail("0.30.0 transcript highlights must expose explicit ordering")

if errors:
    print("Static verification failed:")
    for error in errors:
        print(f"- {error}")
    sys.exit(1)

print(f"Static verification passed ({len(xml_files)} XML/MSBuild files checked).")
print("Project references, package surface, GUI/TUI entrypoints, settings, ability catalogs, and architectural invariants are structurally consistent.")
