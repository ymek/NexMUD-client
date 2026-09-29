# NexMUD 0.27.1

## Terminal typography and transcript density correction

- `NexTranscriptTypography` is now the single owner of transcript font normalization, fixed row height, zero line spacing, zero tracking, and the 20-row density sanity check. The default is 14 px with a 16 px row grid; the existing 8-24 px preference remains supported.
- The World transcript remains one `SelectableTextBlock` with inline ANSI runs. `WorldBufferEntry` boundaries do not create controls, margins, padding, or newlines, so network chunking cannot invent vertical separation. Explicit server blank lines remain byte-for-byte display structure after CRLF normalization.
- Transcript inset belongs to a containing `Border`, not the text control. Timestamp and ANSI/highlight `Run` instances share the same terminal family, preventing per-span font selection from changing terminal geometry.
- The terminal face is selected once per platform: Menlo on macOS, Consolas on Windows, and DejaVu Sans Mono elsewhere. This avoids mixed fallback metrics while preserving monospaced ASCII alignment and bold width.
- `NexTypography` now exposes only the application-wide semantic roles: Display, SectionTitle, Body, BodyStrong, Metadata, CompactData, Monospace, Hud, and HudStrong. `NexSpacing` centralizes Micro, Tight, Normal, and Section spacing. Matching application resources are installed at startup.
- Remaining numeric micro-fonts in the Avalonia UI were replaced with semantic roles. Top navigation is slightly tighter and the right gameplay rail uses compact entity/loadout rhythm without changing its content or hierarchy.
- Regression coverage asserts 14/16 terminal metrics, a 320 px height for 20 default rows, and preservation of adjacent chunks plus explicit blank lines. Static verification enforces the terminal family, typography resources, and synchronized NAWS/render row sizing.
- Design contract: `docs/design/NexMUD-typography-transcript-density-pass.md`. No parser, protocol, semantic-state, command-dispatch, Automation, Mapper, or Jev behavior changed.
- SemVer release: 0.27.1. macOS build: 27001.

# NexMUD 0.27.0

## Input, keybinding, and output transformation architecture

- `ClientInteractionRuntime` is the interaction composition root. `InputPipeline`, `CommandHistoryService`, `CompletionService`, `KeybindingService`, `OutputFrameFactory`, `OutputTransformationService`, `OutputFrameJournal`, and `WorldBuffer` are application services independent of Avalonia controls.
- Manual input records history before deterministic escaped-separator tokenization, resolves Automation aliases before unmatched commands enter `LocalCommandHandler`, and preserves explicit origin through `ScriptCommandRequest` / central command dispatch. Existing provenance enum values remain fixed; Alias and Keybinding append new values.
- History is bounded/session-local and optionally persists through `WorldKnowledgeStore`. Completion is incremental and bounded, preserving MUD-style apostrophes/hyphens/underscores and merging recent output/history with typed state/alias providers.
- Keybindings persist as typed definitions with context/action/priority/enabled state. Resolution prefers the active context then Global, reports equal-priority collisions, and rejects unsafe bare character bindings in text-entry contexts.
- Incoming `GameTextReceived` remains the semantic/raw source. The independent display branch creates immutable `OutputFrame` records and performs prompt presentation, capture, substitution, highlight, gag, and notification processing without mutating the source frame or Core event stream.
- Regex rules enforce a 100 ms match timeout and 4096-character pattern limit. Rule failures and view/notification subscriber failures are isolated from the output worker. Highlight spans are calculated over full rendered text so ANSI run boundaries do not prevent a match.
- `WorldBuffer` is the bounded logical scrollback source for Avalonia and TUI. Search supports case sensitivity and bounded regex; split-output uses the same buffer; timestamps/local echo remain metadata/provenance rather than injected server text.
- The Avalonia World view now incrementally projects `WorldBuffer`, preserves user scroll position, exposes new-output return-to-live affordance, and rebuilds only from bounded logical history. Search runs off the UI thread.
- Settings persist Input/Output preferences, context-aware keybindings, and typed output transformation rules. Existing legacy highlight rules remain supported through the same transformation engine.
- Regression tests cover input ordering/history, completion, keybinding resolution/conflicts, source/rendered separation, ANSI-spanning highlights, replay, local echo, rule failure isolation, scrollback bounds/search, settings round-trip, and provenance compatibility.
- Architecture contract: `docs/architecture/NexMUD-input-keybinding-output-transformation-architecture.md`. Mapper and Jev behavior are not migrated in this slice.
- SemVer release: 0.27.0. macOS build: 27000.

## Jint public continuation-pump correction

- Removed the invalid direct call to Jint 4.16.x `Engine.RunAvailableContinuations()`, which is an internal API and caused `CS1061` in `JevMud.Scripting.Jint`.
- `JintHostBridge.EnqueueWithInvocationAsync` now settles the host-facing Promise and then enters Jint through `Engine.Execute("void 0;", "<nexmud-continuation-pump>")`. Jint's normal public script-evaluation boundary drains pending Promise reactions before returning, while the NexMUD dispatcher still guarantees single-engine serialization.
- The change preserves delayed asynchronous host completion semantics for Mapper and Automation: real I/O can resume JavaScript `await` chains on the mailbox without reflection or unsupported Jint internals.
- Static verification now rejects direct references from the host bridge to Jint internal event-loop APIs and requires the public continuation-pump boundary.
- Existing delayed-pathfinding and delayed-alias regressions remain the behavioral tests for this seam.
- SemVer release: 0.26.5. macOS build: 26005.

# NexMUD 0.26.4

## Shared Jint async continuation + Mapper/Automation reliability correction

- `JintHostBridge` now calls `Engine.RunAvailableContinuations()` after every serialized engine re-entry. Host requests that complete asynchronously therefore settle their SDK Promise and resume the awaiting JavaScript handler in the same mailbox turn.
- Script event subscriptions wait for the owning invocation to reach a terminal state before dequeuing the next event for that subscription. Host completions still re-enter through the mailbox, preserving single-engine serialization while restoring end-to-end FIFO semantics for async handlers.
- `AutoMoveService` again performs the initial route-plan calculation through the typed C# Mapper domain before loading the route module. The immutable initial plan is embedded into the NexMUD-owned orchestration module; later divergence/recovery continues to use `nex.mapper.findPath()` for replanning.
- Automation alias, text-trigger, and state-rule ingress now verifies that `automation.profile` is actually Running. If the runtime is missing or faulted, the compiler attempts one replacement reload from the last settings snapshot; if it still cannot run, the input/match is not falsely treated as successfully automated.
- Added delayed-I/O regressions proving a two-command alias resumes after the first asynchronous command completion and a Mapper route resumes after delayed `findPath()` completion.
- Static verification now guards the shared Jint continuation drain, full async event invocation completion, runtime-health checks across all Automation matcher ingress, C# initial Mapper planning, seeded route-plan orchestration, and both delayed-host regression tests.
- SemVer release: 0.26.4. macOS build: 26004.

# NexMUD 0.26.3

- Fixed the production Mapper route startup failure: `MapperRouteScriptCompiler` used `nex.events.on("mapper.route.execute", ...)`, while `CapabilityScriptHost.EventGate` requires `MapperRouteObserve` for every `mapper.route*` subscription. The generated route package now grants that capability explicitly.
- Audited the complete generated route SDK surface against host gates: `ReadMapper`, `MapperPathfind`, `MapperMove`, `CreateTimers`, `SubscribeEvents`, and `MapperRouteObserve` are all present.
- Added a regression test asserting permission closure for the compiled Mapper route package.
- Added static permission-closure verification tying generated Mapper SDK usage to required manifest capabilities so equivalent package/host-gate mismatches fail before runtime.
- This patch does not change routing policy, pathfinding, recovery, route authority, or UI behavior.
- SemVer release: 0.26.3. macOS build: 26003.

# NexMUD 0.26.2

## Mapper compile-safety correction

- `AutoMoveService` now imports `JevMud.Scripting.Runtime`, resolving the `ScriptOwnerKind.MapperRoute` compile failure in the production route-task fault-correlation path.
- The entire v0.26 Mapper C# delta was audited against the repository type/namespace map and project-reference graph. `ScriptOwnerKind` was the only newly introduced project type missing its namespace import; all other Mapper migration type dependencies resolve through existing project references.
- `scripts/static_verify.py` now verifies scripting-runtime type namespace ownership across the entire C# source tree. Runtime symbols used outside `JevMud.Scripting.Runtime` must import that namespace or use an explicit fully-qualified name.
- The production Map `Go` startup seam remains deterministic: `AutoMoveService` loads/reloads the route module, the module registers `mapper.route.execute`, then the service publishes the route-ID-scoped start event. The generated route module still rejects zero-delay activation startup and keeps only the idempotent 250 ms fallback.
- This patch changes compile safety only; route planning, authority, movement coordination, recovery/replan, pause/resume/abort, and command provenance behavior remain as designed in v0.26.0/v0.26.1.
- SemVer release: 0.26.2. macOS build: 26002.

# NexMUD 0.26.1

## Mapper route startup correction

- Map-node `Go` could enter `Planning route` and never begin navigation because the v0.26.0 internal Mapper module used `nex.timers.after(0, executeRoute)` from inside module activation as its only start mechanism. That path had different activation/scheduler ordering in production than the replay test clock.
- Mapper route startup is now explicit: the Jint module subscribes to the internal `mapper.route.execute` event during activation, `AutoMoveService` waits for `LoadAsync`/`ReloadAsync` to complete, then publishes a route-execution-id-scoped start event through the shared script event hub.
- Route startup is idempotent. A 250 ms `nex.timers.after` callback remains as a fallback, but it is no longer the normal production start path.
- Mapper route runtime identity now binds the compiled script version and per-route owner name to the execution context. Runtime diagnostics/task faults are ignored unless they belong to the active route, preventing stale teardown faults from poisoning a replacement route.
- Added a controlled-clock regression test proving the explicit signal starts the Jint route without advancing timer time.
- SemVer release: 0.26.1. macOS build: 26001.

# NexMUD 0.26.0

## Mapper orchestration migration

- `MapperQueryService` is the read-only domain boundary for current-room lookup and route-plan generation. It uses the existing `MapperReadRepository`; route plans are immutable DTOs and graph/persistence implementation details stay in C#.
- `MapperMovementCoordinator` is the only route-step executor. It permits one movement in flight, emits commands through `IScriptCommands`/`ActionProcessor`, waits for reduced semantic events, classifies structured failure reasons, and never touches Transport.
- `MapperNavigationAuthority` serializes autonomous navigation ownership. Human movement first pauses/settles the active route, while non-human competing movement is rejected until the Mapper releases authority.
- `nex.mapper.currentRoom`, `findPath`, and `move` are exposed through the Jint bootstrap and TypeScript declarations with dedicated Mapper capabilities. Route lifecycle event subscriptions additionally require `MapperRouteObserve`.
- `MapperRouteScriptCompiler` produces the internal `nexmud.mapper.route` module. It runs route behavior through the shared Jint mailbox/scheduler and implements replan-on-unexpected, fixed bounded recovery, delay, completion, and cancellation without graph mutation or raw-output parsing.
- `AutoMoveService` is now the route-control facade only: request resolution, authority, status, pause/resume/abort, Jint module lifecycle, and UI events. The old C# route execution loop no longer exists.
- Route provenance now includes route execution ID, route ID, route step, and route length through the existing central command dispatcher.
- Route runtime state is session-only. Existing durable Mapper graph/knowledge persistence is unchanged.
- Jev navigation and legacy workflow `navigate` requests call the new Mapper route service; Jev reasoning/execution migration remains deferred.
- SemVer release: 0.26.0. macOS build: 26000.

# NexMUD 0.25.0

## Scripting SDK stabilization + Automation runtime migration

- SDK v1 now has the six durable behavioral namespaces required by Automation: events, commands, state, log, timers, and storage. Script-facing events/state remain DTO-only and are deep-frozen before handler code receives them.
- `JintHostBridge` owns async timer invocation identity and completion. `nex.timers.every` is fixed-delay because the scheduler does not begin the next delay until the prior callback invocation, including awaited host work, has completed.
- Storage stays behind `IScriptStorage`; Automation uses profile-scoped SQLite plus compiler-generated `automation:{id}:...` keys. Read and write capabilities are distinct, while legacy write-capability manifests retain read compatibility.
- `AutomationProgram` is the explicit IR boundary. The compiler validates typed triggers/conditions/actions, derives the exact host capabilities, emits deterministic ES2022 JavaScript through `@nexmud/api`, produces IR mapping metadata, and computes a reproducible artifact hash/runtime generation.
- `AutomationRuntimeCompiler` is the strangler boundary. Alias/text/state matching remains deterministic C# domain logic, then publishes immutable internal match DTOs into the script event hub. Commands/actions execute in Jint; recurring and one-shot timers are registered through `nex.timers`.
- Migrated aliases, triggers, state rules, and timers no longer execute through `ClientAutomationService`. That service is now explicitly a compatibility executor for workflow DSL behavior whose Mapper/Jev steps are deferred by this ARD.
- Generated Automation commands flow only through `nex.commands.send` -> capability host -> `ClientScriptCommands` -> `ActionProcessor`; no Automation-to-Transport path exists. Provenance retains Automation id/type/trigger plus Jint script instance/invocation/event/operation ids.
- Reload is replacement-based: new code validates and starts before swap; compile/load failure leaves the prior runtime active. Match DTOs carry runtime-generation identity so overlapping replacement instances cannot both consume alias/text/state matches.
- Generated invocation logic catches faults per Automation program so one definition cannot fault unrelated definitions sharing the profile engine. Diagnostics include Automation identity plus condition/action indices where available.
- Regression coverage includes deterministic compilation/capability derivation, alias execution/provenance, semantic replay with a typed condition, virtual-clock fixed-delay timers/unload cancellation, and SDK TypeScript compilation.
- Architecture contract: `docs/architecture/NexMUD-scripting-sdk-automation-migration-architecture.md`.
- SemVer release: 0.25.0. macOS build: 25000.

# NexMUD 0.24.2

## Workspace typography correction

- Applied the v0.24.1 semantic typography scale to Jev, Scripting, Automation, Map, Codex, Abilities, World context/right-rail panels, and Settings.
- These surfaces now consume `NexTypography.Display`, `SectionTitle`, `Body`, `BodyStrong`, `Metadata`, `Small`, `Mono`, `Hud`, and `HudStrong` according to information hierarchy rather than using local 8.5–10.5 px values.
- Scripting, Automation, Map, Codex, and Settings establish a `NexTypography.Body` inherited control baseline so buttons, input controls, checkboxes, and other unstyled text participate in the same scale.
- Mapper immediate-mode labels were raised to semantic typography roles; node dimensions and map spacing were increased minimally to avoid clipping. No topology, pathfinding, auto-move, or persistence behavior changed.
- Settings sidebar width increased from 190 to 210 px and Codex list width from 280 to 300 px solely to accommodate readable text.
- No parser, canonical state, Automation runtime, Scripting runtime, Jev decision logic, Codex persistence, mapper behavior, transcript layout, or command dispatch architecture changed.
- SemVer release: 0.24.2. macOS build: 24002.

# NexMUD 0.24.1

## Typography + item inspection correction

- Scope is limited to Character/Inventory typography, Item Inspection presentation/interaction, and the compact gameplay HUD typography described by the supplied correction pass.
- Added `NexTypography` semantic roles and application resources. Character identity, paper-doll labels/items, character facts, inventory/search/status text, inspection content, and HUD labels/values now consume those roles instead of local micro-font constants.
- Item Inspection now derives a presentation hierarchy from the existing `ItemIdentification` / Codex data: display identity, slot/type, material/level/weight metadata, whitelisted primary stats, aligned affects/modifiers, compact flags, and source status. No parser/state schema changed.
- Common parser terms are translated for display (`hp`, `moves`, `ac`, hit/damage roll, attributes). Modifier color is applied only for explicitly modeled higher-is-better/lower-is-better fields; unknown mechanics remain neutral.
- Hover is transient and selection is persistent. Paper-doll and inventory hover set temporary inspection state, pointer exit restores the pinned item, and click selection remains independent.
- The inspection column now has a 280 px minimum and the paper-doll/inventory prioritize wrapping/scrolling over font shrinkage.
- Gameplay HUD labels/tactical values use the shared 12.5 px HUD role and 13.5 px strong role; existing 20 px HP/MA/MV bars and layout remain intact.
- Added `docs/design/NexMUD-typography-item-inspection-correction-pass.md` and updated static verification for the correction invariants.
- SemVer release: 0.24.1. macOS build: 24001.

# NexMUD 0.24.0

## Scripting vertical slice

- Implemented the architecture contract in `docs/architecture/NexMUD-scripting-vertical-slice-architecture.md` without replacing the existing Automation, Mapper, or Jev runtimes.
- `character.vitalsChanged` now flows from the semantic Core event bridge through `ScriptEventHub` into one serialized Jint instance. Script event payloads are immutable DTOs and use stable product-facing event identifiers rather than C# type names.
- `@nexmud/api` v1 exposes only the initial public surface: typed events, asynchronous command dispatch, immutable character state, and structured logging. No CLR, transport, filesystem, network, or Jint implementation types are exposed.
- Added `TypeScriptCompiler`, which invokes an installed `tsc` authoring tool with strict isolated configuration and returns compiled JavaScript plus Source Map v3 data. Normal client startup does not invoke or embed Node/npm.
- Host requests carry script/version/instance/invocation/event/operation provenance. `nex.commands.send()` uses the existing `ActionProcessor` command path and never writes to Transport directly. Async completions return only through the script mailbox.
- Script unload cancels active invocations and host requests, disposes subscriptions, and ignores late host completions. Jint runtime failures are constrained and isolated from Core, UI, Transport, and other script instances.
- Added the internal `nexmud.reference.vitals-policy` fixture and integration coverage for real TypeScript compilation, replay, capability denial, provenance, deterministic command output, handler isolation, runaway execution limits, source maps, and unload cancellation.
- SemVer release: 0.24.0. macOS build: 24000.

# NexMUD 0.23.1

## Item inspection build correction

- Fixed `src/JevMud.Gui/ItemInspectionPopover.cs` by importing the root `Avalonia` namespace required by `Thickness` and `CornerRadius`.
- This is a compile-only regression correction; v0.23.0 Character/Inventory behavior and architecture are unchanged.
- Added a static verification guard for the required namespace import.
- SemVer release: 0.23.1. macOS build: 23001.

# NexMUD 0.23.0

## Right rail + Character/Inventory workspace

- Scope is limited to the World right-side gameplay rail and the new Character/Inventory workspace. World transcript, gameplay HUD, command input, protocol architecture, mapper, Automation, Scripting, and Codex internals are not redesigned.
- The right rail now keeps character identity and compact combat/capacity facts visible, replaces equipment prose with a small prioritized loadout, and uses a shared Codex/identify-backed item inspection popover.
- Room intelligence remains canonical-state driven, suppresses empty entity sections, keeps recent activity bounded, compresses room memory, and removes the invalid bare `scan` shortcut. Exit controls provide direction-specific scan actions alongside normal movement.
- Added the Character/Inventory workspace with an Avendar-slot paper doll, occupied/empty slot states, persistent item inspection, searchable observed inventory, capacity/weight/wealth, inventory refresh, and supported identify action. World remains visible beside the workspace.
- Added `InventorySnapshotObserved`, canonical `CharacterState.CarriedItems` / inventory completeness, the Avendar `inventory`/`inv` response capture path, and `AvendarInventoryParser`.
- Added regression tests for carried-item parsing/state reduction and static architecture checks for the workspace, canonical-state projection, shared inspection, and direction-aware scanning.
- SemVer release: 0.23.0. macOS build: 23000.

# NexMUD 0.22.3

## Alias command-batch expansion correction

- Root cause: the UI split the literal user input before `LocalCommandHandler` expanded aliases. Separators introduced by the alias expansion therefore never re-entered the command-batch splitter and were sent to the MUD as one line.
- Added `CommandInputExpander` as the deterministic user-input planning stage shared by Avalonia and TUI. It preserves separator escapes while identifying top-level user commands, expands each alias once, then splits separators produced by the alias expansion.
- `LocalCommandHandler` now receives individual planned commands and remains responsible only for local-command handling and central command dispatch.
- Escaped separators survive positional and `$*` alias substitution and remain literal in the final MUD command.
- Regression tests cover the reported `cake` alias, mixed alias/manual batches, and escaped separator arguments.
- SemVer release: 0.22.3. macOS build: 22003.

# NexMUD 0.22.2

## Terminal cell-height correction

- Scope remains restricted to World transcript terminal metrics.
- Terminal row advance is exactly 1.0x the configured transcript font size rather than 1.05x, matching a terminal cell grid instead of body-copy leading.
- `ApplyTranscriptMetrics` atomically applies font size, line height, and zero line spacing to both transcript panes. This fixes the stale-line-height bug when a user changes transcript font size at runtime.
- NAWS row calculation continues to use the exact same `TranscriptLineHeight` helper as rendering.
- No transcript content rewriting, parser/state changes, ANSI changes, or unrelated UI work is included.
- SemVer release: 0.22.2. macOS build: 22002.

# NexMUD 0.22.1

## Terminal row-density correction

- Scope is restricted to the World transcript's vertical terminal-cell metrics.
- `SelectableTextBlock.LineHeight` is now pinned to 105% of the configured transcript font size, removing the font family's oversized natural leading while leaving `LineSpacing` at zero.
- This restores the intended ASCII-terminal geometry: adjacent `|` rails and multi-line frame glyphs visually form one sign/table instead of looking like unrelated text rows.
- NAWS row calculation uses the exact same line-height helper, keeping advertised terminal rows consistent with the rendered viewport.
- No transcript content rewriting, parser/state changes, font-size migration, or unrelated UI changes are included.
- SemVer release: 0.22.1. macOS build: 22001.

# NexMUD 0.22.0

## Gameplay HUD strip redesign

- Scope is restricted to the gameplay HUD mounted between transcript and command input.
- Consolidated progression, resources, and tactical state into one dense rail with a 48-54 px target height.
- HUD progression is now `LVL` plus exact XP-to-next-level only; total lifetime XP and the XP progress bar are not rendered.
- HP/MA/MV use 20 px semantic bars with values centered inside the bars and labels directly adjacent.
- Position/Combat/Target/Jev use a compact 2x2 state cluster with exception-driven semantic emphasis.
- Added target-state violet token `#9B7DE3`; no theme overrides are applied to HP/MA/MV semantics.
- Added the supplied HUD design contract under `docs/design/` and static verification for the acceptance criteria.
- SemVer release: 0.22.0. macOS build: 22000.

# NexMUD 0.21.0

## Terminal fidelity and shell-density correction

- `MainWindow` now treats workspace navigation as mutually exclusive; World receives active styling only when `ToolView.Context` is actually active.
- Added `TranscriptLineEndingNormalizer` at the GUI presentation seam. It collapses network CRLF to a single display newline, carries a trailing CR across transport chunks, and never modifies the raw text sent through the adapter/parser/logger pipeline.
- Transcript presentation is no-wrap with horizontal scrolling, zero added line spacing, tighter margins, and terminal-specific monospace fallbacks led by Bitstream Vera Sans Mono.
- The World surface, persistent gameplay rail, and command surface shed redundant nested borders/insets. `NexCommandBar` explicitly stretches to the full World width.
- The application-bar connection control now owns both current `host:port` presentation and connect/disconnect action/state.
- `CharacterHudPanel` is an always-visible compact projection of canonical character state. The rejected disclosure and nested Character scrollbar are removed; observed equipment is compressed into wrapped inline text instead of a scrollable slot list.
- Room spacing is reduced without moving parsing/state ownership into the view.
- Added display-normalization regression coverage alongside the existing verbatim-log coverage.
- SemVer release: 0.21.0. macOS build: 21000.
- Runtime C# build/test execution still requires a .NET SDK; static verification remains available in SDK-less environments.

# NexMUD 0.20.1

## Terminal typography density correction

- The game transcript no longer forces a 14-point minimum after settings are applied. Rendering and NAWS sizing share the same 8-24 normalization boundary.
- Transcript settings now expose 8, 9, and 10 in addition to the existing sizes; new profiles default to 11.
- The gameplay transcript uses a dedicated compact terminal font stack (`Monaco`, `Bitstream Vera Sans Mono`, `DejaVu Sans Mono`, `Menlo`, `SFMono-Regular`, `Consolas`, `monospace`) without changing general UI/code typography.
- Transcript line spacing is returned to the font's natural metrics and padding is reduced to preserve more rows/columns while keeping the styled NexMUD frame.
- No transcript bytes, ANSI parsing, command behavior, login state, or gameplay state projection changed.
- SemVer release: 0.20.1. macOS build: 20001.

# NexMUD 0.20.0

## World gameplay rail consolidation

- Removed the duplicate Character workspace/navigation path; the World rail is now the canonical character presentation surface. Its identity header stays compact while detail is explicitly expandable/collapsible.
- Character detail consumes the same `CharacterHudViewModel` as the foveal HUD and now includes inventory capacity plus equipment projection. No transcript parsing was added to Avalonia.
- Removed the redundant World content header. World remains a primary navigation destination and the permanent transcript/command surface.
- Reorganized Room/Context around the current canonical room, exits, populated named entity groups, recent observations, compact memory, and pinned quick actions. Current room state takes precedence over mapper labels.
- Header branding uses the existing 1024×1024 approved application PNG with explicit high-quality downsampling. No replacement art was generated.
- Navigation compacts to icon-only controls when horizontal space is constrained; the navigation viewport is clipped so action controls cannot paint over it.
- Log now follows toolbar utility styling. Connect/Disconnect uses explicit connection-action styling separate from the passive connection-status pill.
- SemVer release: 0.20.0. macOS build: 20000.

# NexMUD 0.19.1

## Main UI regression recovery

- Removed the v0.19.0 generated gameplay decoration bundle and all production references to it. The approved application icon and existing texture assets remain; no replacement artwork was generated.
- `CommandInputPolicy` now models credential submission/transition behavior explicitly, including masking, placeholders, history/completion eligibility, clear-on-submit, local transcript echo policy, and focus restoration.
- `AvendarGameAdapter` now recognizes partial login/password prompts, tracks input mode explicitly, reacts to Telnet ECHO transitions, and finalizes buffered command responses at Telnet prompt boundaries.
- Score data still enters the application only through `CharacterScoreObserved -> StateReducer -> StateSnapshot`. Both gameplay HUD and Character panel consume the same `GameplayShellViewModel.Character` projection.
- Room observations now carry `RecentObservations` into canonical `RoomState`; entity classifier output supplies displayable canonical occupant/fixture names while preserving the original observation text.
- Added populated Randolph and Adventurer's Lounge regression assertions across parser, reducer, and projection boundaries.
- SemVer release: 0.19.1. macOS build: 19001.

# NexMUD 0.19.0

## Production gameplay surface

- Added `GameplayHudPanel` directly between the transcript and command bar. It owns foveal level/XP/vital/combat/target/Jev presentation and deliberately renders XP progress as indeterminate until level-boundary data exists.
- Reduced `CharacterHudPanel` to stable identity and character facts, and changed `RoomContextPanel` to prioritize room name, exits, named nonempty entity groups, recent observations, memory, and quick actions.
- `GameplayShellViewModels` now exposes title-aware character identity, alignment, current/base attribute values, combat stats, exploration, wealth, and exact XP strings without fabricating a percentage.
- `AvendarScoreParser` now accepts both boxed legacy score output and the supplied plain score layout, including explicit `Name:`, slash-form inventory capacity, and multi-denomination wealth.
- Added `CommandInputPolicy` in the presentation layer so masking, completion/history eligibility, and password-history exclusion are deterministic and testable outside Avalonia.
- The GUI explicitly clears and refocuses command input on password-to-normal transitions.
- Replaced drawn frame diamonds/corners on the production gameplay surface with raster assets under `src/JevMud.Gui/Assets/Ornaments`. Structural frame lines remain Avalonia drawing primitives.
- Added a real image-backed character crest, application mark, active-navigation ornament, Jev sigil, frame/panel corners, panel junction, and major divider.
- Updated default navigation semantics for Automation and Log.
- Added the populated Randolph score fixture and regression coverage for score parsing plus password input policy.
- SemVer release: 0.19.0. macOS build: 19000.

## 0.18.5 compile correction

- Qualified the vector icon backing type and construction as `Avalonia.Controls.Shapes.Path` in `NexMudIcons`, removing ambiguity with `System.IO.Path` from implicit SDK usings.
- Static verification now requires the qualified type.

## 0.18.4 prescriptive World visual system

- Established the reusable NexMUD gameplay design system and default-World composition specified by the visual implementation contract.
- Added layered frame primitives, ornamental geometry, coherent vector iconography, five resource-driven accents, typography roles, resource bars, HUD components, room/context composition, and premium command/navigation chrome.
- Secondary workspace redesign remained intentionally out of scope.


## Default World visual reconstruction

- Introduced `WorldVisualTheme`, intentionally scoped to primary gameplay chrome so this pass does not restyle secondary workspaces.
- Main gameplay shell now uses layered dark surfaces and nested muted/brass frames rather than flat black panels and one-pixel accent outlines.
- Character HUD and Room / Context were rebuilt around persistent immutable gameplay view models; no domain/runtime ownership moved into Avalonia.
- Transcript remains the dominant surface and retains ANSI rendering, prompt/user-command distinction, scrollback bounding, live-tail split, completion, and logging behavior.
- Command bar is visually integrated into the World frame with larger monospace input and a stronger primary Send action.
- Default gameplay rail target width is 390–425 px (370–445 bounds); tool workspace ratios remain unchanged.
- No Codex/Mapper/Automation/Scripting/Settings workspace implementation was redesigned in this slice.
- macOS build: 18003.


# NexMUD 0.18.2

## Single-window World/workspace correction

- Replaced the rejected default auxiliary-window model with a single-window shell. `MainWindow` permanently owns World and a shared right-side `ContentControl` workspace.
- Removed `AuxiliaryWindowHost` from normal tool navigation. Mapper, Codex, Automation, Scripting, Character, Abilities, Jev, and transcript search are hosted inside the primary window.
- Added product-defined workspace ratios matching the redesign brief: Default World fixed gameplay rail, Codex 58/42, Mapper 50/50, Automation 47/53, Scripting 44/56, Character/Jev 60/40, Abilities 55/45.
- Replaced the top-level settings window with `SettingsWorkspace : UserControl`, hosted inside a bounded modal sheet/scrim owned by `MainWindow`.
- World remains mounted behind Settings and remains mounted during every right-workspace transition.
- World now owns an explicit header, transcript region, and permanently attached command bar in the same visual tree.
- Refined persistent Character HUD and Room / Context panels for denser ARPG/analytics presentation and semantic resource/state color.
- Updated theme tokens to the redesign palette: obsidian `#0B0D10`, midnight `#0F1B2A`, slate `#1E293B`, parchment `#E6DDC6`, muted `#9CA3AF`, brass `#D4A85B`, cyan `#22D4BF`, success `#22C55E`, warning `#F59E0B`, ember `#EF4444`.
- Removed stale UI copy saying deep tools open in separate windows.
- Rewrote the Avalonia architecture contract around five application states and explicit view-model/window/layout ownership.
- Static verification now rejects `AuxiliaryWindowHost`, top-level Settings navigation, and stale multi-window copy; verifies in-app workspace ratios, settings workspace type, required palette tokens, and gameplay-shell nullable/import safety before reporting success.
- SemVer release: 0.18.2. macOS build: 18002.

# NexMUD 0.18.1

## Gameplay shell compile correction

- `GameplayShellViewModels.cs` now imports `JevMud.Contracts.Events`, resolving the `ConnectionStatus` reference used by the persistent connection presentation model.
- `AttributeScore` lookup now uses nullable-aware narrowing before formatting, satisfying the repository's warnings-as-errors policy without suppressions.
- No world-first UI architecture semantics changed.
- SemVer release: 0.18.1. macOS build: 18001.

# NexMUD 0.18.0

## World-first Avalonia shell

- MainWindow now owns a fixed gameplay composition instead of a replaceable contextual dock: transcript + command bar on the left, persistent character HUD + room/context rail on the right.
- Added immutable presentation projections (`GameplayShellViewModel`, `CharacterHudViewModel`, `RoomContextViewModel`) and dedicated long-lived gameplay panels.
- Added bounded persistent XP meter, vitals, position/combat/target/Jev state, core attributes, room exits/entities/observations, durable room memory, and room-metadata enrichment.
- Room metadata loading is asynchronous and room-identity guarded so late completions cannot paint metadata for a room the player has already left.
- `MapperKnowledgeChanged` refreshes room memory without coupling the panels to the persistence implementation.
- Added `AuxiliaryWindowHost` for non-modal tool surfaces; explorer workspaces preserve their own lifecycle and cancellation while World remains visible.
- Added Scripting runtime/module surface; it reads language-neutral runtime snapshots and does not expose Jint objects.
- Settings is now non-modal and owned by the gameplay window.
- New semantic palette uses obsidian/midnight/slate surfaces, parchment text, brass structure, cyan interaction, and resource/status colors.
- Added `docs/architecture/NexMUD-avalonia-world-first-ui-architecture.md`.
- Removed legacy combat-context auto-focus from the gameplay shell; persistent telemetry makes it redundant and passive events must not steal focus.
- Same-room knowledge commits force guarded room-metadata reloads; persistent room memory is projected only when its room identity matches Core state.
- SemVer release: 0.18.0. macOS build: 18000.

# NexMUD 0.17.0

## Product identity

- Product-facing identity is now NexMUD. macOS packaging emits `NexMUD.app` / `NexMUD`, bundle id `ai.typesafe.nexmud`, build `17001`.
- Added new NexMUD `N` icon assets for macOS, Windows/Avalonia, and source PNG.
- Application-data and macOS Keychain access retain explicit legacy JevMUD migration/fallback paths. Internal `JevMud.*` code identifiers remain intentionally unchanged in this cut.

## Jint adapter

- Added isolated `JevMud.Scripting.Jint`; it alone owns the exact Jint `4.16.3` package dependency.
- `JintEngineFactory` centralizes sandbox posture and execution constraints. General CLR access, reflection, CLR writes, operator interop, dynamic string compilation, CommonJS require, and blocking agent suspension remain closed.
- Every independently loaded package receives its own engine, execution scope, host bridge, request/subscription/timer ownership, and bounded single-reader mailbox. No continuation or event producer directly re-enters Jint.
- Added explicit source/module preflight limits and controlled package-relative module resolution. `@nexmud/api` is registered in-memory; arbitrary file/network/npm module resolution is unavailable.
- Added private request/response bridge and frozen public `nex` API. Capability enforcement remains in C# host adapters.
- Added replacement hot reload: the candidate engine fully initializes before the runtime atomically replaces the old instance. Failed initialization leaves the old instance running.
- Jint JavaScript time now delegates to the injected NexMUD scripting scheduler/clock, so `Date.now()` and `new Date()` follow replayable runtime time instead of uncontrolled wall clock.
- Shutdown now closes the mailbox writer immediately when an instance stops accepting work, making dispatcher draining deterministic rather than relying on a timeout/cancellation fallback.

## TypeScript authoring boundary

- Added language-neutral compiler artifacts/manifests/diagnostics/cache-key contracts to `JevMud.Scripting`.
- Added `JevMud.Scripting.TypeScript` with `@nexmud/api` declarations and compile-cache infrastructure. It depends only on language-neutral scripting contracts and does not depend on Jint.
- A concrete TypeScript compiler adapter and source-map translation are still pending. This prerelease therefore does not yet claim direct `.ts` execution acceptance; it establishes the correct replaceable boundary while Jint executes validated compiled JavaScript packages.

## Verification

- Static verification pins Jint `4.16.3`, rejects Jint package leakage, enforces adapter dependency direction, requires the sandbox/mailbox/clock/hot-reload invariants, verifies the TypeScript boundary has no external runtime/compiler package, and checks NexMUD application identity assets.
- Added runtime tests for sandboxed load, per-script engine isolation, dynamic string compilation rejection, controlled JavaScript time, failed hot-reload preservation, and TypeScript declaration/API-version alignment.
- This execution environment still lacks the .NET SDK. `./scripts/run-macos-app.sh` remains the authoritative compilation/runtime validation.

---

# JevMUD 0.16.0-alpha.4

## Mapper directed-edge state correction

- Mapper viewport now groups reciprocal directed exits by room pair for the base connection.
- The connection line is neutral (or route-highlighted); each directed exit renders its own direction and blocked/door state near its source.
- This prevents a stale or genuinely one-way block from visually overwriting the traversable reverse direction.
- Added durable knowledge regression coverage proving successful traversal heals prior `Blocked` state, clears `block_reason`, and records the destination.
- Static verification requires reciprocal-edge grouping and rejects the old whole-edge blocked rendering path.
- macOS CFBundleVersion: 16004.

## alpha.3 empty-input correction

- Removed whitespace validation from `ClientScriptCommands.SendAsync`.
- The shared command adapter now rejects only `null`; `string.Empty` is valid MUD input.
- Added regression coverage which sends an empty user command through `ClientScriptCommands` -> `ActionProcessor` -> sender.
- macOS CFBundleVersion: 16003.


## alpha.2 compile correction

- Fixed `AutomationWorkspace.HandleEvent` where independent `is not` checks were incorrectly joined with the C# pattern combinator `and`.
- The filter now uses boolean `&&` between complete type-test expressions.
- Static verification now rejects the malformed pattern form and requires the corrected expressions.
- macOS CFBundleVersion: 16002.

# JevMUD 0.16.0-alpha.1

## Shared programmable execution foundation

This release implements the first architecture cut defined by `docs/architecture/JevMUD-scripting-platform-architecture.md`. Domain truth and infrastructure remain strongly typed; programmable orchestration moves onto shared runtime primitives.

### Scripting platform

- Introduced dependency-free `JevMud.Scripting` contracts: runtime/module/context, capability host, events, scheduler, clock, storage, permissions, and structured execution ownership.
- `ManagedScriptRuntime` is a first-party bootstrap adapter only. It deliberately does not select JavaScript, Lua, Python, or another public authoring language.
- `ScriptExecutionSupervisor` owns hierarchical scopes and cancellation for application, module, automation, Jev, mapper, and future plugin work. Owned task failures are surfaced through one diagnostic channel.
- `ScriptEventHub` provides serialized per-subscription delivery with bounded backpressure and owner-bound teardown.
- Script time is injected through `IScriptClock`/`IScriptScheduler`; migrated behavior no longer directly reads wall-clock time or creates ad hoc delays.

### Host and security boundary

- Client adapters expose state, semantic events, command dispatch, mapper/pathfinding, Codex search, storage, timers, UI notifications, and logging through capability-gated interfaces.
- Script/module persistence lives in independent `script-state.db` storage keyed by module identity. No script-facing raw access to mapper/Codex/application tables is exposed.
- `ActionProcessor` remains the only transport-facing command dispatcher. Commands now carry ownership/provenance through dispatch/execution events and durable history.

### First-party migrations

- Declarative recurring command timers compile into a managed runtime module (`automation.timers`). One-shot timers and the broader workflow DSL remain on the compatibility path in this cut.
- Automation workflows now use shared command, scheduling, and owned execution primitives rather than `ActionProcessor`, `Task.Delay`, and independent cancellation sources.
- Mapper auto-move now uses the shared command/scheduler/execution services while typed graph/pathfinding/persistence remain unchanged.
- Jev keeps typed decision policy but uses the shared command/scheduler/execution services for selected-action execution and outcome waits. Disabling Jev cancels Jev-owned runtime work without affecting automation or mapper owners.

### Deferred deliberately

- Concrete JavaScript/Lua runtime selection.
- User-authored script loader/editor/permissions UI.
- Full trigger/game-rule/workflow compiler migration.
- Replacement of all `AutoMoveService` policy with a runtime module.
- Plugin packaging and privileged capabilities.

### Verification

- Added tests for capability enforcement, structured cancellation, centralized task faults, sequential event delivery, namespaced storage, and command provenance.
- Static verification enforces downward dependency direction and rejects direct action/timing primitives in migrated behavior orchestration.
- This execution environment does not include the .NET SDK, so `./scripts/run-macos-app.sh` remains the authoritative compiler/runtime check.
- macOS CFBundleVersion: 16001.

---

# JevMUD 0.15.0-alpha.23

## alpha.23 compile correction

- Added the missing `MapperRecordingEnabled()` and `MapperPersistenceEnabled()` production helpers used by `WorldKnowledgeStore`.
- `MapperRecordingEnabled`: `Enabled && AutoMap`.
- `MapperPersistenceEnabled`: `Enabled && AutoMap && PersistKnowledge`.
- Extended static verification to require both declarations.


## Mapper movement transaction hardening

- Added action-correlated movement response tracking in the Avendar adapter.
- Added `NavigationResponseCompleted` so non-standard denials can terminate pending navigation without requiring phrase-specific parsing.
- Room observations remain the only evidence of successful traversal.
- Denied movement responses are removed before subsequent room observations can consume them.
- Explicit `NavigationFailed` output is not double-consumed at response completion.
- Preserves ordered batched movement and correctly handles denied-then-successful batches.
- Added reducer and durable knowledge regression coverage for stale movement poisoning.
- macOS CFBundleVersion: 15023.

# JevMUD 0.15.0-alpha.14

- Restored the public `WorldKnowledgeStore` Codex and room-metadata read APIs required by the GUI and tests: `SearchCodexAsync`, `GetCodexEntryAsync`, and `GetRoomMetadataAsync`.
- Hardened `scripts/static_verify.py` so call sites can no longer masquerade as implementations of those APIs.

## Automation/mapping hardening

- Fixed the C# CS0136 compile failure in workflow `wait` handling caused by an `out` variable named `condition` extending across the method scope.

This pass audits the interrupted `0.15.0-alpha.11` automation/mapping completion work and fixes lifecycle edges found during verification.

- Workflow starts now enforce `MaxConcurrentWorkflows` for both automatic and manual/operator starts.
- Manual workflow execution respects the automation master setting and disabled groups instead of starting an execution which will later fail at its first command.
- One-shot workflow state is consumed only after successful admission.
- `Continue` failure mode reports a recoverable running-state detail rather than an internally contradictory terminal `Failed` followed by `Completed`.
- Human override timing uses atomic millisecond state across the event reader and workflow/background workers.
- `navigate <query>` reuses the route chosen by nearest-destination resolution, eliminating a redundant second SQLite route plan and guaranteeing execution starts from the route which made the candidate reachable.
- Destination search no longer lets broad room/area matches consume the entire result budget before MOB/object/fixture/item-source candidates are queried.
- Regression coverage was added for concurrency admission and continue-mode lifecycle semantics.

`python3 scripts/static_verify.py` passes. This environment does not provide the .NET SDK, so local .NET 10 build/test execution remains required for compiler/runtime verification.

---

# JevMUD 0.15.0-alpha.11

## Automation and mapping completion pass

This prerelease completes the first-party automation/mapping baseline before generalized extensibility. The goal is to prove the event, state, action, workflow, navigation, and knowledge contracts in real first-party code rather than guessing a public plugin API prematurely.

### Automation runtime

- `ClientAutomationService` is now a deterministic runtime for text triggers, state rules, timers, and declarative workflows. All emitted commands still converge on `ActionProcessor`; automation has no direct transport path.
- State expressions support boolean composition (including grouped negation), comparisons, persisted `var.*` values, event fields, room/entity/interactable/exit predicates, skills/spells/equipment, vitals, inventory, connection/combat state, and related modeled facts.
- Workflows support `send`, `delay`, `wait`, `wait-event`, `if`, `unless`, `retry`, `assert`, `navigate`, `set`, `unset`, `jev`, and `stop`. `retry` is deliberately bounded to ten retries and may include a bounded delay.
- Workflow variables can persist across sessions. Event context survives waits and can be interpolated through `${event.*}` / `${...}` templates.
- Workflows can trigger from state predicates and/or semantic event type names and support priorities, groups, cooldowns, one-shot execution, failure modes, cancellation, and bounded concurrency.
- Automation command limiting is backpressure, not loss: automation waits for a command slot instead of silently discarding actions. Trigger/rule/timer commands also queue through the human-override window rather than disappearing while the player has control.
- Human input opens a configurable override window. Deterministic automation waits rather than racing explicit player commands.
- Workflows can call mapper-backed `navigate <query>` and may explicitly escalate unresolved combat/navigation/recovery decisions to Jev. Jev escalation fails the workflow step when no decision is produced instead of reporting false success. Deterministic behavior remains the default; Jev is the ambiguity fallback.
- `AutomationWorkspace` provides persistent operator visibility into configured workflows, active executions, variables, and recent automation activity.

### Mapping and navigation

- `MapperReadRepository` is now the single implementation for mapper neighborhood projection, destination search, nearest-destination selection, and route planning. Legacy `WorldKnowledgeStore` compatibility entry points delegate to it after schema initialization.
- Search targets include rooms, MOB observations, fixtures/objects, and item-source provenance. Nearest-route selection evaluates reachable candidates rather than assuming the first match is the useful one.
- Route planning honors room avoid flags plus configured avoided areas, terrain, MOB names, blocked exits, unknown traversability, closed doors, and preference for confirmed traversable exits. The graph remains directed, so one-way and custom traversal commands are naturally represented.
- Route steps carry observed door state, traversability, and blocker reason for execution/UI decisions.
- Auto-move uses one in-flight movement step and advances only after room arrival is observed. It supports pause/resume/step/stop, combat interruption, optional combat resume, human/manual navigation preemption, bounded arrival watchdogs, and bounded re-planning. Cancelling a workflow navigation request explicitly stops the route it started.
- Route lookup/re-planning occurs outside the navigator gate so Stop/Pause/manual override remain immediate while SQLite is searching. Stale plan generations cannot restart movement after preemption.
- Failed movement is persistent knowledge. If the server explicitly reports a closed door, the current pending direction is persisted as a closed/blocked exit instead of deleting the edge.
- Optional automatic door opening is configurable through Mapper settings. The default template is `open {direction}`, but the feature is disabled by default because the client does not assume target-MUD command grammar without explicit configuration. The navigator waits for `RoomExitStateChanged(Open)` before sending the movement step.
- Route preview surfaces closed doors/unconfirmed exits so automated traversal constraints are understandable before execution.

### Extensibility boundary

No generalized scripting/plugin API is exposed yet. These first-party components intentionally exercise the seams a later extension layer is expected to use: semantic events, immutable state, deterministic actions, workflow execution, route queries/navigation, persistent knowledge, and Jev decision escalation.

### Verification

`python3 scripts/static_verify.py` passes. Additional regression cases were added around workflows, retries, mapper constraints, persistent navigation failure learning, and settings. This execution environment still has no .NET SDK, so `dotnet build` / `dotnet test` cannot be executed here; local .NET 10 compilation remains authoritative.

---

# JevMUD 0.15.0-alpha.10

## Protocol subsystem completion

This prerelease completes the agreed first-party protocol baseline before the next automation/mapping pass. The transport remains deliberately first-party rather than introducing a plugin protocol API prematurely.

- Stateful Telnet option negotiation tracks local/remote capabilities, rejects unsupported options once, emits lifecycle transitions, and avoids repeated negotiation/activation loops.
- GMCP activation sends `Core.Hello` and `Core.Supports.Set`; inbound messages remain structured module/payload events and outbound GMCP is available through `TcpMudTransport`.
- MSDP uses a recursive value model (`MsdpScalar`, `MsdpArray`, `MsdpTable`), discovers `REPORTABLE_VARIABLES`, and REPORTs only supported values JevMUD currently consumes.
- MSSP retains repeated values as lists.
- TTYPE/MTTS reports JevMUD identity, configured terminal type, then an MTTS bit vector reflecting actual ANSI/UTF-8/256-color/truecolor/MNES/TLS capability.
- NEW-ENVIRON implements filtered SEND/IS responses with escaping and MUD-standard metadata variables.
- CHARSET accepts UTF-8 when offered and explicitly rejects unsupported translation tables.
- NAWS tracks live transcript dimensions and sends updates after negotiation when the viewport changes.
- EOR and GA become explicit prompt-boundary events for deterministic automation.
- MCCP2 decompression failures become fatal protocol errors; orderly compression-stream completion no longer masquerades as remote socket closure.
- Subnegotiation payloads are capped at 64 KiB and malformed frames become observable `ProtocolError` events.
- Protocol toggles are centralized under Settings → Protocols and apply on the next connection.

### Verification

`tests/JevMud.Tests` now includes regression cases for negotiation deduplication, MTTS sequencing/reset, CHARSET UTF-8/translation-table handling, NEW-ENVIRON filtering, nested MSDP, discovery-driven MSDP REPORTs, bounded oversized subnegotiation, EOR/GA boundaries, and EOR settings persistence. `scripts/static_verify.py` is also run before packaging. This environment still has no .NET SDK, so compilation/runtime verification must be performed on a .NET 10 development machine.

## v0.15.0-alpha.9

## Codex MOB identity and loot provenance

- MOB Codex identity is now area-scoped: same-named MOBs in the same mapped area aggregate into one entry, while the same name in another area remains distinct. Rooms without area metadata remain room-scoped to avoid false global merges.
- Item provenance is persisted as item ↔ MOB ↔ room observations. Explicit MOB drops and items acquired from corpses are captured from Avendar semantic text.
- Items observed through loot become browseable before identification. Item details expose known MOB sources with route actions; MOB details expose observed loot and can open the related item.
- Area aggregation is derived at read time from room metadata, so correcting/assigning an area later automatically changes Codex grouping without rewriting historical observations.
- Real combat-loot transcript coverage now verifies the complete Avendar sequence: `EnemyKilled`, XP gain, equipment acquired from the explicitly named corpse, currency gains, and corpse destruction.
- Corpse destruction is modeled explicitly with `CorpseDestroyed`; the reducer removes a matching known corpse and marks room contents partial, preventing deterministic automation from acting on stale corpse state.

This prerelease extends the persistent Codex model with area-scoped MOB identity and durable loot provenance while preserving the persistent workspace architecture introduced in `0.15.0-alpha.6`.

## 0.15.0-alpha.6 - Mapper and Codex subsystem rearchitecture

The previous Mapper/Codex implementations remained coupled to `MainWindow.RenderDock()`, which allowed normal game-state/event rendering to replace explorer controls and made UI lifecycle behavior difficult to reason about. This release replaces both features as isolated persistent workspaces rather than applying another incremental performance patch.

### Codex

- `CodexWorkspace` owns a single persistent visual tree, virtualized `ListBox`, observable result collection, selection, scroll state, search state, detail pane, and cancellation lifecycle.
- Live render/state ticks only update cheap contextual text. They do not reconstruct the result list or detail surface.
- Knowledge-ingest events mark the Codex as having newer data but do not automatically requery or replace user-visible results.
- Browse/search and detail SQLite reads execute on worker tasks; UI-thread work is limited to applying completed immutable results.

### Mapper

- `MapperWorkspace` owns Mapper lifecycle and interaction state independently from `MainWindow`.
- `MapperViewport` is a single immediate-mode drawing control with pan/zoom/hit-testing. It does not create a control per room/edge or a graph-sized backing `Canvas`.
- `MapperReadRepository` uses a private, read-only SQLite connection (`query_only`) with short busy/command timeouts and no schema initialization.
- Neighborhood projection is bounded by room and edge budgets. Route finding traverses the persisted graph incrementally rather than materializing the lifetime world graph.
- Graph acquisition, scene construction, search, route planning, and mapper metadata persistence execute away from the Avalonia UI thread.
- Opening Mapper paints its shell immediately; topology loading begins only after activation and is bounded by a cancellation/timeout boundary.

### Shell integration

- `MainWindow.RenderDock()` now treats Mapper/Codex as persistent controls. It may attach them but does not construct or refresh them.
- Ordinary game-state renders skip dock reconstruction while either explorer is active.
- Explicit activate/deactivate lifecycle cancels outstanding work when the explorer closes or switches.
- Static verification now rejects legacy Mapper/Codex render methods or explorer calls back into `RenderDock()`.

### Verification

- `scripts/static_verify.py` passes.
- Mapper read behavior has regression coverage in `tests/JevMud.Tests`.
- The execution environment does not contain the .NET SDK, so compilation and runtime UI verification must be performed on a .NET 10 development machine.

## 0.15.0-alpha.5 - Stable Codex browsing and mapper rendering isolation

- Codex no longer reconstructs its browse list when live item/help knowledge arrives. Result selection and scroll position remain owned by the existing `ListBox` instance.
- Codex detail loads update a dedicated detail host rather than rerendering the entire explorer. Explicit category/search operations remain the only result-list refresh path.
- Mapper rendering no longer materializes room `Border` controls, edge `Line` controls, labels, and tooltips. A single `MapperSurface` custom control draws the visible graph and performs room hit-testing.
- Mapper graph acquisition is cancelled after four seconds and reports a recoverable load message instead of allowing knowledge-store work to monopolize the explorer lifecycle.
- `GetMapGraphAsync` enforces an edge budget derived from the visible-room budget, preventing malformed or unusually high-cardinality exit history from escaping the mapper's bounded-work contract.

## 0.15.0-alpha.4 - Mapper UI-thread freeze, entity classification, command batching

- Mapper graph and route SQLite queries are dispatched from the GUI onto worker tasks. Microsoft.Data.Sqlite async calls can execute synchronously, so calling them directly from the Avalonia UI flow could still beach-ball the application. Mapper loading is also scheduled only after the explorer shell renders; control construction no longer starts graph I/O re-entrantly.
- Mapper rendering is now a fixed-size local viewport instead of an unbounded retained Canvas. Only rooms/edges inside the local coordinate window create visuals.
- Avendar fixture classification no longer depends on indentation for clear fixture-subject lines such as `A gurgling fountain bubbles here.`
- Codex MOB browse/detail and mapper MOB destination search now include only persisted `Occupant` entities. Fixtures/objects/corpses are no longer presented as MOBs. Existing persisted occupant rows which deterministically match known fixtures are repaired once when the knowledge schema initializes.
- Added configurable human-input command separator (default `;`, blank disables) with backslash escaping, shared by GUI and TUI input. Example: `n;n;n;n;w;open chest`.
- Durable mapper inference now keeps an ordered queue of pending movement attempts, so a human command batch such as `n;n;n;w` records each observed room transition instead of overwriting all but the last direction. Navigation failure consumes only the failed pending movement.

## 0.15.0-alpha.3 - Mapper/Codex launch freeze fix

- Mapper launch now renders a bounded 48-room neighborhood and expands explicitly in chunks.
- Visual map coordinates are ranked/compacted before rendering so long chains cannot create pathological giant Avalonia canvases.
- Mapper graph requests are cancellable and respect the current render budget.
- Codex opening is query-free: overview/summary renders immediately.
- Cross-category Codex search requires at least two characters; category browsing remains available with an empty search.
- Entity/combat category browse aggregates bounded recent windows rather than grouping lifetime history on every launch.
- Codex search no longer rebuilds the whole dock just to display a loading state on every keystroke.

# JevMUD Automation, Protocols, Mapper, and UX Implementation

## UX overhaul

- Rebuilt Mapper as a browse-first visual workspace instead of a metadata form. It renders the observed world graph around the current room, highlights the current room/target/planned route/avoid rooms, labels non-cardinal exits, and keeps transcript space available alongside the explorer.
- Mapper search now finds both remembered rooms and remembered MOBs/entities. Selecting either immediately plans a route to the observed room.
- Added controlled auto-move with Go, Pause, Resume, Step, and Stop. Movement executes one known edge at a time and waits for an observed room transition before advancing.
- Auto-move pauses on combat by default, optionally resumes after combat, re-plans on route deviation/failure, rate-limits confirmed steps, and has a configurable arrival-confirmation timeout so a missing MUD response cannot strand the navigator indefinitely.
- Room metadata and special-exit editing remain available but are secondary dialogs rather than the mapper's primary interface.
- Rebuilt Codex as a searchable two-pane browser with categories for Rooms, MOBs, Items, Abilities, and Combat. Details expose durable observations, known locations, raw references where useful, and direct route actions.
- Map and Codex use wider explorer surfaces without overwriting the user's normal contextual dock width. They are no longer rebuilt for every unrelated state tick, which preserves focus/scroll state and reduces UI churn.
- Added the Jev master control to the main toolbar, command palette, and Settings. It disables Jev decisions/execution immediately while preserving the configured per-domain authority matrix.

## Automation

- Added state-aware deterministic game rules in addition to aliases, text triggers, key bindings, and timers.
- Game-rule conditions support `&&` plus boolean/string/numeric comparisons over modeled state such as `combat.active`, `hp.percent`, `mana.percent`, `room.name`, `room.corpses`, `position`, and level/XP fields.
- Added automation groups, trigger priorities, per-trigger cooldowns, stop-processing semantics, and rolling-buffer trigger scope for multiline output.
- Added a global deterministic automation enable switch and command-rate guard.
- All automated commands continue through the existing Rules-sourced action pipeline.
- Raw transcript output is never gagged or rewritten by automation.

## Protocols

- Added settings and negotiation controls for NAWS, GMCP, MSDP, MSSP, MCCP2, CHARSET, NEW-ENVIRON, and MTTS.
- Added MSDP and MSSP frame parsing and typed events.
- Added conservative MSDP REPORT requests for common vitals/room/opponent fields.
- Added MCCP2 zlib stream handoff, including compressed bytes already received in the Telnet negotiation buffer.
- Added UTF-8 CHARSET acceptance and neutral terminal capability reporting.
- Connection creation is centralized through `JevMudRuntime.CreateConnectionOptions` so GUI, CLI commands, and TUI use the same protocol configuration.

Protocol setting changes apply on the next connection because Telnet capability negotiation is connection-scoped.

## Mapper/data model

- Promoted persistent SQLite room/edge knowledge into a reusable mapper API.
- Added persistent room metadata: label, area/zone, notes, and route-avoidance flag.
- Added room/entity destination search, local graph projection, and breadth-first route planning over known edges.
- Route planning honors maximum depth, blocked/unknown traversal policy, and avoided rooms.
- Added manual/special exit creation/override for non-cardinal or corrected topology.

## Settings

Dedicated Settings sections now cover:

- Automation: engine enablement, command rate, rolling buffer, disabled groups, richer triggers, state-aware game rules, aliases, timers, and key bindings.
- Protocols: individual Telnet/OOB capability toggles.
- Mapper: graph recording/persistence, route constraints, visual graph depth/size, auto-move enablement, combat behavior, step delay, arrival timeout, and re-plan limits.
- Jev: master enable/disable control, credentials/model, preset, and per-domain authority.

## Verification

`python3 scripts/static_verify.py` passes after the changes. Additional regression tests were added for durable mapper entity/graph/route behavior, Jev master-state persistence, and preserving the authority profile while Jev is disabled.

The execution environment used for this implementation does not contain the .NET SDK and has no external network resolution, so `dotnet build` / `dotnet test` could not be executed here. The source should be built and tests run in the normal JevMUD development environment before release.

## 0.15.0-alpha.2 - Mapper/Codex responsiveness

- Knowledge-store schema initialization now runs once per store lifetime; connection-local SQLite pragmas still run per connection.
- Mapper graph loading now performs bounded frontier queries instead of materializing the complete persisted exit graph.
- Route planning now traverses indexed frontier batches instead of loading all known exits before BFS.
- Mapper layout computation is moved off the Avalonia UI thread, map room nodes use lighter-weight controls, and auto-move progress no longer rebuilds the full map on every step.
- Codex result presentation now uses a virtualized `ListBox`; broad searches are capped to 60 results and stale detail loads are cancelled.
- Added SQLite indexes for room recency, reverse exits, entity recency/name, and combat recency.
- Visual mapper room limit is capped at 250 to prevent accidental pathological UI workloads; the map remains a local navigable neighborhood rather than an all-world render.