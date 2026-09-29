# NexMUD Client v0.27.1

## v0.27.1 - Terminal typography and transcript density correction

- Reworked World transcript typography around an explicit terminal line grid: new profiles default to 14 px text with a 16 px row height, zero added line spacing, and zero letter spacing while retaining the existing 8-24 px user range.
- Replaced the transcript fallback-family stack with one platform-selected monospace face per OS (Menlo on macOS, Consolas on Windows, DejaVu Sans Mono elsewhere) so ANSI weight/style runs cannot select inconsistent base font metrics.
- Kept the transcript as one continuous selectable inline surface and moved viewport inset spacing to the containing host. Network frame boundaries add no layout spacing; only server-provided newlines create terminal rows, and explicit blank lines remain intact.
- Timestamp and ANSI/highlight runs now use the same terminal font family. Highlighting, local echo, split output, searching, selection, logging, prompt slurp, and scrolling retain their existing behavior and share the same row geometry.
- Centralized the application typography roles (Display, Section title, Body, Body strong, Metadata, Compact data, Monospace, HUD, HUD strong) and Micro/Tight/Normal/Section spacing resources. Removed remaining functional 8-10 px text from the Avalonia surface.
- Tightened navigation and gameplay-rail rhythm without changing information architecture. Loadout rows now target a 20 px compact row and entity lists use compact-data typography.
- Added regression coverage for the 20-row transcript density target and for preserving server line structure across independent network chunks, including explicit blank lines.
- Added the design contract at `docs/design/NexMUD-typography-transcript-density-pass.md`. Parser behavior, ANSI semantics, command handling, HUD structure, navigation structure, and Jev behavior are unchanged.
- SemVer release: 0.27.1. macOS build: 27001.


## v0.27.0 - Input, keybinding, and output transformation architecture

- Introduced a first-class interaction layer instead of UI-local command/transcript behavior. Manual input now flows through typed history, command tokenization, Automation alias resolution, and central command dispatch while preserving User/Alias/Keybinding provenance.
- Added session-local command history with optional SQLite persistence, in-progress-buffer restoration, bounded retention, configurable de-duplication, and no generated-command pollution.
- Added incremental Tab/Shift-Tab completion over recent rendered output, manual history, aliases, exits, room contents, inventory/equipment, skills, and spells. MUD identifiers preserve apostrophes, hyphens, and underscores.
- Added typed, persisted, context-aware keybindings with deterministic priority/fallback, explicit conflict detection, safe text-entry behavior, and application actions for history/completion, scroll/search, commands, Automation, Jev toggle, panes, and Mapper pause/resume/abort.
- Introduced immutable `OutputFrame` source records, a bounded raw/source journal, deterministic display-only transformation rules, and a bounded logical `WorldBuffer`. Semantic parsing/Automation continue to consume the original event stream; gag/substitution cannot rewrite Core meaning.
- Added output highlight, gag, substitution, structured regex capture, notify/beep hooks, bounded-regex diagnostics, ANSI-composed rendering, timestamp metadata, provenance-styled local echo, rendered scrollback search, scroll-lock/new-output affordance, and split-output projection over the same logical buffer.
- Avalonia and TUI now project the shared `WorldBuffer`; they no longer own raw-output transformation. Avalonia input delegates history/completion/alias/dispatch behavior to interaction services rather than maintaining parallel state.
- Added Settings sections for Input/Output, Keybindings, and Output Rules plus persisted configuration for separator/history/completion/local echo/timestamps/scrollback/split behavior/rules. Multi-action rule data remains preserved by the editor.
- Added regression coverage for escaped command separators, history restoration/isolation, completion cycling, keybinding conflicts, source-vs-rendered separation, gag/substitute/capture/highlight behavior, ANSI-run-spanning highlights, replay determinism, local echo metadata, output-rule fault isolation, buffer subscriber isolation, bounded scrollback/search, settings persistence, and provenance enum compatibility.
- Added the architecture contract at `docs/architecture/NexMUD-input-keybinding-output-transformation-architecture.md`. Jev behavior is intentionally unchanged.
- SemVer release: 0.27.0. macOS build: 27000.

## v0.26.5 - Jint public continuation-pump correction

- Fixed the v0.26.4 build failure caused by calling Jint 4.16.x internal `Engine.RunAvailableContinuations()` from `JintHostBridge`.
- Promise continuation pumping now stays inside Jint's supported public API: after a serialized host resolve/reject re-entry, NexMUD executes a no-op script, whose normal Jint `ScriptEvaluation` path drains queued Promise reactions before returning.
- Preserved the delayed-host reliability correction for Mapper pathfinding and Automation aliases without reflection, internal Jint access, or a second execution thread.
- Added static guards forbidding direct use of Jint event-loop internals (`RunAvailableContinuations`, event-loop drain methods, and `EventLoop`) from the host bridge.
- Retained full async-handler FIFO, C# initial Mapper planning, runtime-health checks for Automation ingress, route authority, capability enforcement, and central command dispatch.
- SemVer release: 0.26.5. macOS build: 26005.


## v0.26.4 - Shared scripting async reliability correction

- Fixed the shared Jint host bridge so Promise continuations are explicitly drained after asynchronous host completions re-enter the serialized script mailbox. Real delayed I/O can now resume `await` chains instead of behaving differently from already-completed test Tasks.
- Event subscription delivery now remains FIFO through completion of the full asynchronous JavaScript handler, not only through its initial entry into Jint.
- Restored the Mapper domain boundary for initial navigation planning: C# calculates the first immutable route plan before the route module starts, while Jint continues to own movement orchestration and uses `nex.mapper.findPath()` for replans.
- Hardened Automation ingress so aliases, text triggers, and state rules require a running shared Automation runtime. A faulted/unloaded runtime gets one replacement reload attempt rather than silently consuming or dropping matched behavior.
- Added delayed-host regression coverage for a multi-action alias and delayed Mapper pathfinding so asynchronous production timing is exercised instead of only immediate fake-host completion.
- Preserved central command dispatch, Mapper authority/provenance, Automation provenance, capability enforcement, and the v0.26 Mapper orchestration architecture.
- SemVer release: 0.26.4. macOS build: 26004.


## v0.26.3 - Mapper route capability correction

- Fixed route startup failure caused by the generated Mapper route module subscribing to `mapper.route.execute` without the required `MapperRouteObserve` capability.
- Added `MapperRouteObserve` to the NexMUD-owned route module permission set so the production EventGate accepts its route-start subscription.
- Added Mapper generated-code permission-closure verification covering `currentRoom`, `findPath`, `move`, timers, event subscriptions, and `mapper.route.*` observation.
- Added a regression test that asserts the compiled Mapper route package grants every capability required by its generated SDK usage.
- Audited all current Mapper route SDK calls against their C# host capability gates; no other missing Mapper grants were found.
- SemVer release: 0.26.3. macOS build: 26003.


## v0.26.2 - Mapper compile-safety correction

- Fixed the v0.26.1 `AutoMoveService` build failure by importing `JevMud.Scripting.Runtime`, which defines `ScriptOwnerKind` used by route-task fault correlation.
- Audited every C# file introduced or changed by the v0.26 Mapper orchestration slice against project-defined type namespaces and project-reference reachability; no other newly introduced unresolved cross-project type references remain.
- Added repository-level static verification for the scripting-runtime symbol family (`ScriptOwnerKind`, `ScriptModuleId`, runtime profiles/status/contracts, and related DTOs) so Client/GUI/Automation/Mapper/Jev code cannot reference those types without the defining namespace or an explicit fully-qualified name.
- Retained and re-verified the v0.26.1 route-start seam: Map `Go` loads/subscribes the Jint route module before publishing `mapper.route.execute`; the zero-delay activation timer remains prohibited and the bounded fallback is secondary only.
- Preserved Mapper authority, semantic movement handling, replay behavior, central command dispatch, and route lifecycle semantics unchanged.
- SemVer release: 0.26.2. macOS build: 26002.


## v0.26.1 - Mapper route startup correction

- Fixed Map `Go` routes becoming stranded in `Planning route` before the first Mapper command. Route execution is now started by an explicit internal `mapper.route.execute` event published only after the Jint module is fully loaded and subscribed.
- Replaced the activation-time zero-delay timer as the primary startup mechanism with an idempotent post-load start signal. A bounded 250 ms runtime timer remains only as a defensive fallback.
- Correlated Mapper script diagnostics and supervised-task faults to the active route's compiled script version/runtime owner so teardown faults from an older route cannot fail a replacement route.
- Added a production-startup regression test using controlled script time to prove a loaded route remains idle until the explicit start signal, then begins orchestration without advancing fallback timer time.
- Preserved the v0.26.0 Mapper domain/runtime boundary, central command dispatch, route authority, replay behavior, and semantic movement handling.
- SemVer release: 0.26.1. macOS build: 26001.


## v0.26.0 - Mapper orchestration on the shared scripting runtime

- Moved Mapper route execution out of the bespoke C# auto-move workflow and into a NexMUD-owned Jint orchestration module. Map topology, room identity, pathfinding, reconciliation, and persistence remain typed C# domain services.
- Added the preview `nex.mapper` SDK namespace with capability-gated `currentRoom()`, `findPath()`, and semantic `move()` operations. Scripts receive immutable room/route DTOs and never receive graph repositories or SQLite handles.
- Added a single-flight `MapperMovementCoordinator` that emits Mapper commands only through central command dispatch and resolves movement from semantic room/navigation events as `moved`, `blocked`, `unexpected`, `timeout`, `disconnected`, or `cancelled`.
- Added explicit route authority and Mapper provenance (`RouteExecutionId`, route ID, step, total steps). Manual player movement pauses autonomous routing before the user command is dispatched; competing Automation/Jev/script movement is rejected while Mapper owns navigation.
- Added route lifecycle orchestration for planning, stepping, pause/resume, abort, disconnect cancellation, unexpected-room replanning, bounded standing/closed-door recovery, movement timeouts, and destination confirmation. Resume detects stale pre-pause steps and replans from authoritative current-room state.
- Added semantic Mapper lifecycle events and route failure reasons for diagnostics/UI/replay consumers. Route script execution is supervised through the existing timer/mailbox path so script faults release navigation authority without affecting connection, UI, Automation, or other scripts.
- Existing Jev/workflow navigation requests now terminate at the new Mapper route-control service; the Jev execution compatibility path itself remains in place for the next architecture slice.
- Added the supplied architecture contract at `docs/architecture/NexMUD-mapper-orchestration-architecture.md` plus structural regression guards for the Mapper SDK, movement coordinator, Jint route module, authority, provenance, and compatibility removal.
- SemVer release: 0.26.0. macOS build: 26000.

## v0.25.0 - Scripting SDK stabilization + Automation runtime migration

- Stabilized Script API v1 around `nex.events`, `nex.commands`, `nex.state`, `nex.log`, `nex.timers`, and `nex.storage`; semantic event/state DTOs are immutable at the JavaScript boundary and every host operation remains capability-gated in C#.
- Added NexMUD-owned `delay`, one-shot, and fixed-delay recurring timers backed by the shared scheduler/clock, including deterministic cancellation, Jint-mailbox callback serialization, and replay/virtual-time support.
- Added persistent JSON script storage with explicit read/write capabilities. Generated Automation state is isolated under `automation:{automationId}:...` keys inside the profile-scoped script-storage namespace.
- Added a typed, serializable Automation IR plus deterministic IR -> JavaScript compiler with validation, capability derivation, source/IR mapping metadata, reproducible content hashes, and an explicit ES2022 target.
- Migrated aliases, text triggers, state rules, and command timers away from their independent legacy executors. Domain-specific matching remains in C# where appropriate, while matched behavior executes through the shared Jint runtime and public NexMUD SDK.
- Preserved Automation provenance through the central command dispatcher (`AutomationId`, type, trigger/event, script instance, invocation, and parent operation). Non-Automation scripts cannot spoof Automation metadata.
- Added replacement-style hot reload, profile-generation guards, runtime-owned timer cleanup, per-Automation fault isolation, condition/action-index diagnostics, stable definition identities, and deterministic same-priority ordering.
- Existing workflow definitions containing Mapper/Jev operations remain on the compatibility workflow executor because `nex.mapper` and Jev execution migration are explicit non-goals of this architecture slice. No new Mapper/Jev scripting surface was introduced.
- Added compiler, replayed semantic-condition, alias/provenance, virtual recurring-timer, TypeScript SDK, permission, cancellation, and generated-source regression coverage.
- Added the supplied architecture contract at `docs/architecture/NexMUD-scripting-sdk-automation-migration-architecture.md`.
- SemVer release: 0.25.0. macOS build: 25000.

## v0.24.2 - Workspace typography correction

- Extended the centralized `NexTypography.*` hierarchy across Jev, Scripting, Automation, Map, Codex, Abilities, World context/right-rail panels, and every Settings page.
- Removed remaining 8.5–10.5 px workspace micro-fonts from those surfaces. Primary content now uses readable body roles, section headings use the shared section role, and secondary/status text uses the shared metadata/small roles.
- Gave Scripting, Automation, Map, and Codex workspaces a readable inherited body baseline so buttons, search fields, and otherwise unstyled controls no longer fall back to the old compact typography.
- Increased map node/edge label typography and adjusted node geometry/spacing only enough to prevent clipping at the readable scale; mapper topology, interaction, and routing behavior are unchanged.
- Increased Settings navigation/page/section/form typography and widened the settings navigation column slightly to support the new scale without truncation.
- Updated World context, room/entity, combat, right-rail, Jev decision, and Abilities presentation to the same semantic hierarchy without changing domain/state behavior or transcript layout.
- SemVer release: 0.24.2. macOS build: 24002.

## v0.24.1 - Typography + item inspection correction

- Replaced Character/Inventory and gameplay-HUD micro-fonts with centralized semantic `NexTypography.*` resources, including readable paper-doll slot/item, inventory, metadata, tactical-state, and resource-value roles.
- Rebuilt Item Inspection as a player-facing ARPG/MUD detail panel with item identity, compact material/level/weight metadata, type-specific primary stats, aligned modifiers, concise flag chips, observation status, and the existing Identify action.
- Added explicit player-facing labels for common identify affects (`hp` -> `Hit Points`, `moves` -> `Movement`, `ac` -> `Armor Class`) and semantic modifier color only where benefit direction is known.
- Added temporary hover inspection plus independent persistent click selection for both paper-doll and inventory items; leaving hover restores the pinned selection.
- Gave the inspection surface a practical minimum width and allowed paper-doll item names to wrap instead of shrinking them to unreadable text.
- Kept parsing/state architecture, paper-doll slot semantics, World transcript layout, and unrelated workspaces unchanged.
- Added the correction-pass contract at `docs/design/NexMUD-typography-item-inspection-correction-pass.md` and structural guards for typography, hover/pin behavior, item presentation, and HUD sizing.
- SemVer release: 0.24.1. macOS build: 24001.


## v0.24.0 - Scripting vertical slice

- Completed the first end-to-end NexMUD scripting behavior path: semantic event -> typed TypeScript handler -> serialized Jint runtime -> capability-gated host SDK -> central command dispatcher.
- Added the product-facing `@nexmud/api` v1 declarations for `nex.events`, `nex.commands`, `nex.state`, and `nex.log`, including the stable `character.vitalsChanged` event contract and immutable resource snapshots.
- Added a concrete external-`tsc` TypeScript compiler boundary with strict type checking, source maps, compile caching, and engine-neutral JavaScript output.
- Added invocation/event/operation identity, command provenance, cancellation, late-completion protection, runtime source-map diagnostics, and script/invocation status observability.
- Added the internal `nexmud.reference.vitals-policy` package plus replay, permission-denial, fault-isolation, runaway-script, cancellation, source-map, and central-dispatch regression coverage. The reference policy is not enabled during normal gameplay.
- Added the supplied architecture contract at `docs/architecture/NexMUD-scripting-vertical-slice-architecture.md`.
- SemVer release: 0.24.0. macOS build: 24000.

## v0.23.1 - Item inspection build correction

- Fixed the Character/Inventory item inspection component failing to compile because Avalonia root types (`Thickness` and `CornerRadius`) were referenced without importing the `Avalonia` namespace.
- No runtime behavior, UI design, parser/state behavior, or workspace scope changed from v0.23.0.
- Added static verification for the required Avalonia namespace import.
- SemVer release: 0.23.1. macOS build: 23001.

## v0.23.0 - Right rail and Character/Inventory workspace

- Reworked the World right rail into a compact gameplay intelligence surface: character identity/stats, a prioritized equipped loadout, current room/exits/entities, recent observations, compact room memory, and only context-free actions.
- Equipped loadout items now use one shared hover inspection presentation backed by canonical equipment state plus existing identify/Codex knowledge. Missing item facts remain unknown rather than being fabricated.
- Removed the invalid bare `Scan` rail action. Exit controls retain primary movement behavior and expose direction-aware `Go <direction>` / `Scan <direction>` context actions.
- Restored Character as a real workspace destination rather than a duplicate summary tab. World remains visible while the Character/Inventory workspace is open.
- Added a text-first Avendar equipment paper doll using observed wear-slot semantics, with occupied/empty states, selection, shared item inspection, and supported `id <item>` inspection.
- Added canonical carried-inventory observation (`inventory` / `inv`) and parsing, plus searchable carried items, item/weight capacity, wealth, and refresh in the Character workspace.
- Views consume canonical state and existing knowledge APIs; no transcript parsing or persistence access was added to UI controls.
- Added parser/state regression coverage and the supplied design contract at `docs/design/NexMUD-right-rail-character-inventory-redesign.md`.
- SemVer release: 0.23.0. macOS build: 23000.

## v0.22.3 - Alias command-batch expansion

- Fixed aliases whose expansion contains the configured command separator. An alias such as `cake -> open pack;get cake pack;eat cake;close pack` now dispatches four ordered MUD commands instead of sending the entire expansion as one command.
- Added a single user-input expansion stage shared by the Avalonia and TUI clients: user command batches are identified first without destroying escape intent, aliases expand per command, and alias-produced batches are then split for dispatch.
- Escaped separators remain literal through alias arguments, so an input such as `sayx hello\;world` can expand to one `say hello;world` command rather than two commands.
- Local/login/password/editor command behavior and the central command dispatcher are unchanged.
- Added regression coverage for multi-command alias expansion, mixed alias/manual batches, and escaped separators in alias arguments.
- SemVer release: 0.22.3. macOS build: 22003.

## v0.22.2 - Terminal cell-height correction

- Transcript row advance now matches the configured font size exactly, removing body-copy leading from the terminal grid.
- Applying transcript font settings now reapplies font size, line height, and line spacing together to both transcript panes; changing font size can no longer leave a stale row height behind.
- NAWS row sizing uses the same terminal cell-height calculation as rendering.
- No transcript bytes, ANSI behavior, parsing/state logic, or non-transcript UI changed.
- SemVer release: 0.22.2. macOS build: 22002.

## v0.22.1 - Terminal row-density correction

- Fixed excessive vertical leading in the World transcript by giving terminal text an explicit line-cell height of 105% of the configured font size instead of accepting the font's much roomier natural metrics.
- Avendar ASCII frames, including the vertical side rails in `score`, now render as a compact terminal grid with only a small inter-row gap rather than widely separated body-text lines.
- NAWS terminal row reporting now uses the same transcript line-height calculation, so the server's advertised terminal geometry matches what NexMUD actually renders.
- Raw transcript text, ANSI processing, parsing, logging, configured font size, horizontal spacing, and every non-transcript surface are unchanged.
- SemVer release: 0.22.1. macOS build: 22001.

## v0.22.0 - Dense gameplay HUD rail

- Rebuilt only the gameplay HUD strip between the World transcript and command input as one compact 48-54 px instrument rail; no other main-window surface is redesigned in this release.
- Progression now shows only `LVL <n>` and `NEXT <n> XP`. Lifetime XP and the inaccurate/indeterminate XP progress bar are removed from the HUD.
- HP, MA, and MV keep explicit labels immediately beside substantial 20 px resource bars, with the numeric `current/max` value rendered inside each bar. Semantic resource colors remain fixed across themes.
- Position, Combat, Target, and Jev now form one compact 2x2 tactical cluster without redundant labels or separate cards. Normal states are subdued; abnormal position, active combat, populated targets, and active Jev states gain semantic emphasis.
- Combat displays `ENGAGED` when active and `Clear` otherwise; empty target state displays `No target`; Jev projects the compact `Off`, `Ready`, `Acting`, and `Deciding` states while retaining detailed active information as a tooltip.
- Added the dedicated target accent (`#9B7DE3`) and enhanced the shared resource-bar control with a centered in-bar value treatment while preserving its existing fill semantics.
- Added `docs/design/NexMUD-gameplay-hud-strip-redesign.md` as the implementation contract and updated static/regression verification for the compact HUD projection.
- SemVer release: 0.22.0. macOS build: 22000.

## v0.21.0 - Terminal fidelity and gameplay-shell density

- Corrected top-navigation activation so only the current workspace is highlighted; World no longer remains visually active while Automation, Map, Codex, Scripting, Abilities, or Jev is selected.
- Added display-only CRLF normalization before ANSI presentation, including fragmented CR/LF transport chunks. Raw server text used by adapters, parsing, and transcript logging is unchanged.
- Switched the gameplay transcript to a terminal-style no-wrap surface with horizontal scrolling, tighter insets, zero added line spacing, and a Bitstream Vera Sans Mono / DejaVu / Monaco / Menlo fallback order. The existing 8-24 point size range remains supported.
- Flattened redundant World/rail chrome and reduced splitter, transcript, HUD, Character, Room, and command-bar spacing so gameplay information receives more of the window.
- Made the command surface explicitly stretch edge-to-edge across the World column.
- Consolidated connection status, `host:port`, connect, and disconnect into one stateful application-bar control.
- Replaced the Character disclosure/scroll model with a compact always-visible summary. Identity, attributes, combat stats, exploration, wealth, inventory capacity, conditions, and observed equipment are presented compactly without expanding the pane or scrolling inside it.
- Tightened Room information spacing while preserving the current hierarchy and named entity projection.
- Added regression coverage for CRLF display normalization without changing verbatim transcript logging.
- SemVer release: 0.21.0. macOS build: 21000.


## v0.20.1 - Terminal typography density correction

- Removed the hidden 14-point transcript rendering floor. The configured transcript size now reaches the game surface directly, normalized only to the supported 8-24 range.
- Extended transcript text-size choices down to 8, 9, and 10 for dense terminal-style play. New profiles default to 11 instead of 14. Existing explicit user preferences remain intact.
- Added a terminal-specific monospace fallback stack led by Monaco on macOS, with Bitstream Vera Sans Mono / DejaVu Sans Mono / Menlo / SF Mono / Consolas fallbacks. The interface/code font stack remains unchanged.
- Removed extra transcript line spacing and reduced transcript padding so server ASCII art, score tables, and room output occupy a terminal-like grid closer to established MUD clients.
- Kept server whitespace intact; this is presentation-only and does not rewrite transcript content.
- Added settings regression coverage for 8-point persistence and lower/upper transcript-size normalization.
- SemVer release: 0.20.1. macOS build: 20001.

## v0.20.0 - World gameplay rail consolidation

- Removed the redundant Character top-navigation destination. Character information now lives in the persistent World gameplay rail, where its identity summary remains visible and its deeper attributes, combat facts, inventory capacity, conditions, and equipment can be expanded/collapsed in place.
- Removed the redundant in-content `WORLD` title while retaining World as the primary navigation destination and permanent transcript surface.
- Reworked the Room rail into an information-first surface: actual room identity and metadata, immediate exits, populated visible-entity groups with names, recent observations, compact durable memory, and pinned quick actions. Empty entity groups no longer consume visual space.
- Corrected room projection precedence so the current canonical room name cannot be replaced by mapper metadata labels.
- Switched the application-header mark to the existing approved high-resolution `Assets/app-icon.png` with high-quality bitmap interpolation. No new visual assets were generated.
- Added responsive top-navigation compaction so narrow windows switch navigation labels to icon-only controls instead of allowing the right-side status/actions to cover navigation.
- Restyled Log as a toolbar utility and Connect/Disconnect as explicit state-aware actions consistent with the rest of the application bar.
- Extended the canonical Character projection with item/weight capacity and equipment summaries for the expandable World pane.
- Added/extended static and projection regression checks for the consolidated navigation, approved application icon, room hierarchy, and shared canonical character state.
- SemVer release: 0.20.0. macOS build: 20000.

## v0.19.1 - Main UI regression recovery and data integrity

- Removed the generated v0.19.0 gameplay ornament/portrait asset set and returned the gameplay frame to neutral structural chrome. No replacement filigree or portrait assets are introduced in this recovery pass.
- Reworked command input around explicit `LoginName`, `LoginPassword`, and normal command state. Login credentials are excluded from command history, password input remains masked/visible, sensitive commands are not locally echoed, and credential transitions clear and refocus the input deterministically.
- Avendar login parsing now observes partial prompts such as a non-newline-terminated `Password:` and uses Telnet ECHO negotiation to enter/leave sensitive input mode without timeout heuristics.
- Avendar response capture now finalizes at Telnet prompt boundaries, preventing a complete `score` response from remaining buffered while telemetry HUD state updates independently.
- `score` continues through the canonical reducer before either gameplay projection consumes it. The Randolph fixture now covers identity, age metadata, all six current/base attributes, vitals, XP, exploration, combat stats, AC assessment, inventory capacity, and wealth.
- Room observations now preserve canonical entity names and recent dynamic observations in canonical room state. Room exits come from the observed room block when available; the room rail projects room identity, environment, exits, named people/fixtures, recent activity, memory, and actions without parsing transcript text in the view model.
- Added regression coverage for login/password transitions, password history/transcript exclusion policy, prompt-boundary score completion, canonical score reduction, canonical room reduction, and shared gameplay projections.
- This is a regression-recovery patch only. No secondary workspace redesign or new visual asset production is included.
- SemVer release: 0.19.1. macOS build: 19001.

## v0.19.0 - Production gameplay surface correction

- Moved critical gameplay telemetry into a dense foveal HUD directly between transcript output and command entry. Level, exact XP values, HP, MA, MV, position, combat, target, and Jev state now remain in the player's primary visual path.
- Removed duplicated live vitals from the right-side Character surface and rebuilt it around actual character identity, alignment, six attributes, hitroll, damroll, saves, AC, exploration, and wealth.
- Extended Avendar score parsing for explicit `Name:` output, base/current attributes such as `Dex: 20(18)`, slash-form inventory limits, alignment, combat stats, and multi-denomination wealth. XP percentage is no longer inferred from insufficient data.
- Rebuilt the room intelligence rail around room identity, exits, named visible entities, recent observations, memory, and quick actions. Empty entity categories no longer consume full cards.
- Replaced programmer-art frame/crest substitutions with image-backed application mark, character crest, filigree corners, junctions, divider, navigation ornament, and Jev sigil assets derived from the approved obsidian/brass visual direction.
- Simplified the command surface to prompt, input, and Send. Up/Down history and Tab/Shift+Tab completion remain keyboard behaviors. Input focus uses the active accent rather than fixed cyan.
- Hardened password-mode presentation and history policy. Sensitive input stays visible but masked, is excluded from history/completion, clears on submission, and restores normal unmasked focused input when the session returns to normal mode.
- Corrected primary icon semantics so Automation uses a workflow graph while Settings retains the gear metaphor; Log now uses a document/transcript icon.
- Added regression fixtures/tests for the supplied Randolph score data and password input policy.
- Scope remains the main gameplay surface only. Secondary workspaces were not redesigned.
- SemVer release: 0.19.0. macOS build: 19000.

## v0.18.5 - Icon library compile correction

- Fixed the `NexMudIcons` compile failure caused by the ambiguous `Path` symbol under .NET implicit usings. The icon primitive now explicitly uses `Avalonia.Controls.Shapes.Path`.
- Added static verification for the fully qualified Avalonia vector path type.
- No visual, layout, runtime, or interaction changes from the 0.18.4 prescriptive visual implementation pass.
- SemVer release: 0.18.5. macOS build: 18005.

## v0.18.4 - Prescriptive default-World design system

- Replaced the earlier flat gameplay visual treatment with the prescribed NexMUD game-client design system: layered metallic framing, reusable ornamental/frame primitives, coherent vector icons, resource-driven accent themes, stronger typography hierarchy, substantial HUD meters, composed Room/Context information surfaces, and designed command/navigation chrome.
- Scope remained limited to the default World composition and its shared design primitives; secondary workspaces were not redesigned.
- Added five accent variants over the common dark base theme: Ember/Brass, Arcane/Cyan, Mystic/Violet, Blood/Crimson, and Verdant/Emerald.
- SemVer release: 0.18.4. macOS build: 18004.


## v0.18.3 - Default World visual reconstruction

- Rebuilt the default World view against the approved visual reference rather than preserving the alpha styling.
- Added a gameplay-only visual system with layered obsidian/midnight surfaces, restrained antique-brass framing, parchment text, and semantic cyan/status colors.
- Re-composed the persistent Character HUD as an ARPG-style information surface with identity/sigil treatment, prominent XP progression, substantial HP/MA/MV meters, gameplay-state readouts, and a compact attribute grid.
- Re-composed Room / Context into a stronger hierarchy with room identity, exits, three-column entity summaries, observations, memory, and quick actions.
- Strengthened the World frame, transcript header, transcript spacing, top navigation, connection controls, and integrated command bar while preserving transcript/runtime behavior.
- This pass intentionally does not redesign Codex, Mapper, Automation, Scripting, Settings, or other right-side workspaces.
- SemVer release: 0.18.3. macOS build: 18003.


## v0.18.2 - Single-window integrated World workspace

- Corrected the 0.18.0 interaction model: NexMUD now remains one coherent primary application window during normal gameplay instead of opening Mapper, Codex, Automation, Scripting, Character, Abilities, Jev, or transcript search as separate OS tool windows.
- World is permanently mounted on the left. Top navigation changes the in-app right workspace while transcript state and the attached command bar remain live and visible.
- Default World mode uses a bounded 340–420 px gameplay rail with persistent Character HUD and Room / Context intelligence.
- Codex uses an approximately 58/42 World/tool split; Mapper 50/50; Automation 47/53; Scripting 44/56; Character/Jev 60/40; Abilities 55/45.
- Settings is now a large in-app sheet over a dimmed but still mounted World instead of an OS settings window.
- Character presentation keeps level/XP progression, HP/MA/MV, position, combat, target, Jev state, conditions, and the six core attributes visible without leaving World.
- Room context keeps exits, people/MOBs, objects, fixtures, corpses, observations, durable room memory, route/visit metadata, and quick actions in one scannable surface.
- The command surface is permanently attached beneath World and adds explicit Send and history controls while preserving empty input, command history, completion, password handling, editor/pager behavior, and central command provenance.
- Updated the application palette to the redesign brief's obsidian/midnight/slate/parchment/brass/cyan semantic tokens and retained resource/status colors for meaning rather than decoration.
- Rewrote `docs/architecture/NexMUD-avalonia-world-first-ui-architecture.md` around the five required states: Default World, Codex, Mapper, Automation/Scripting, and Settings, including widths, hierarchy, scrolling, resizing, and minimum dimensions.
- Removed stale product copy describing deep tools as separate non-modal windows and strengthened static verification to reject a return to the default multi-window model.
- SemVer release: 0.18.2. macOS build: 18002.

## v0.18.1 - Gameplay shell compile correction

- Added the missing `JevMud.Contracts.Events` import required for `ConnectionStatus` in `GameplayShellViewModels`.
- Corrected nullable `Dictionary.TryGetValue` handling for reference-type `AttributeScore` values under `Nullable=enable` / `TreatWarningsAsErrors=true`.
- No layout, interaction, or architecture behavior changed from 0.18.0.
- macOS CFBundleVersion: 18001.

## v0.18.0 - World-first Avalonia UI architecture

- Replaced the alpha-era peer-workspace shell with a world-first gameplay composition: the transcript and command line remain the primary surface while character telemetry and room/context intelligence are persistently visible.
- Added immutable `GameplayShellViewModel`, `CharacterHudViewModel`, and `RoomContextViewModel` projections so persistent gameplay views consume presentation data rather than application/domain objects.
- Added long-lived `CharacterHudPanel` and `RoomContextPanel` controls with HP/MA/MV, XP progression, combat/target/Jev state, core attributes, exits, occupants, objects, fixtures, observations, durable room memory, and quick actions.
- Added non-modal `AuxiliaryWindowHost`; Mapper, Codex, Automation, Scripting, Character detail, Abilities, Jev inspection, and transcript search no longer replace the World surface.
- Settings now opens as an owned non-modal window and applies saved changes back to the live gameplay shell.
- Added `ScriptingWorkspace` for runtime/module visibility without coupling the GUI to Jint internals.
- Adopted the obsidian/midnight/parchment/brass/cyan semantic design tokens from the NexMUD redesign brief.
- Added `docs/architecture/NexMUD-avalonia-world-first-ui-architecture.md` as the concrete desktop presentation architecture.
- Passive combat/state changes no longer open or focus a context tool; the persistent HUD carries those states without stealing keyboard focus. Same-room knowledge changes refresh room metadata safely, and durable room memory is identity-guarded against asynchronous writer lag.
- Command palette navigation now treats World as a focus target and exposes Scripting as a secondary tool rather than another primary page.
- Versioning now follows SemVer directly: this feature slice is `0.18.0`; corrections continue as `0.18.1`, `0.18.2`, etc.
- macOS CFBundleVersion: 18000.


## v0.17.0 - NexMUD rename and Jint runtime foundation

- Renamed the product-facing application from **JevMUD** to **NexMUD**. The macOS bundle is now `NexMUD.app`, executable/product metadata use NexMUD, the bundle identifier is `ai.typesafe.nexmud`, and existing application-data/keychain state is migrated from legacy JevMUD locations where practical. Internal `JevMud.*` namespaces/project names remain for source/binary continuity.
- Replaced the application icon with the new NexMUD `N` mark across PNG, Windows ICO, and macOS ICNS assets.
- Added `JevMud.Scripting.Jint` with an exact Jint `4.16.3` dependency. No other project references the Jint NuGet package.
- Added one isolated Jint engine per compiled script package, a bounded single-consumer mailbox, explicit lifecycle states, deterministic unload, and replacement-style hot reload which keeps the old instance alive if the replacement fails to initialize.
- Centralized hardened engine creation: CLR globals/reflection/writes/operator interop remain disabled, `eval`/`new Function` string compilation is disabled, CommonJS `require` is disabled, blocking `Atomics.wait` is disabled, and execution is bounded by timeout, statements, memory, recursion, execution-stack, regex, promise, array, JSON, source, AST-estimate, module-count, module-size, module-depth, module-hop, and mailbox limits.
- Added controlled in-memory ES-module registration with package-root traversal rejection and `@nexmud/api` as the only built-in scripting module. There is no `node_modules`, filesystem discovery, network import, or Node/Bun/Deno runtime dependency.
- Added the private host bridge and frozen public `nex` API for events, commands, state, mapper, Codex, storage, timers, UI notifications, and logging. Async host work returns through request IDs and is resolved/rejected only by the owning script mailbox.
- Jint event callbacks and timer callbacks re-enter the engine only through its serialized dispatcher; outstanding requests, subscriptions, and timers are cancelled during instance shutdown.
- JavaScript `Date.now()` / `new Date()` now use NexMUD's injected script clock, aligning direct JavaScript time reads with timers and future event-journal replay.
- Added `JevMud.Scripting.TypeScript` as an engine-independent TypeScript authoring boundary with deterministic compiler contracts/cache keys and versioned `@nexmud/api` declarations. A concrete TypeScript compiler adapter is deliberately **not** claimed in this prerelease; Jint currently consumes validated compiled JavaScript packages.
- Added Jint integration coverage for sandboxed activation, engine isolation, dynamic-code rejection, controlled JavaScript time, failed hot-reload preservation, and TypeScript API declaration versioning.
- macOS CFBundleVersion: 17001.

## v0.16.0-alpha.4 - Directional exit-state rendering correction

- Reciprocal room exits are now rendered as one neutral room-to-room connection with direction-specific state annotations near each source room. A blocked/closed/locked state in one direction no longer paints the entire physical connection as blocked.
- Successful traversal remains authoritative evidence of traversability and clears stale blocked/door state for the traversed directed exit.
- Added regression coverage for a previously blocked exit becoming traversable after a successful move.
- macOS CFBundleVersion: 16004.

## v0.16.0-alpha.3 - Empty MUD input restoration

- Restored empty-string command support through the shared scripting/command arbitration path.
- Empty input now reaches the MUD unchanged, allowing pager prompts such as `[Hit Return to continue]`.
- Null command values remain invalid.
- Added regression coverage through `ClientScriptCommands` and static verification for the boundary.
- macOS CFBundleVersion: 16003.

## v0.16.0-alpha.2 - Automation workspace compile correction

- Corrected the Automation workspace runtime-event filter to combine independent C# type tests with boolean `&&` rather than pattern-combinator `and`.
- Added static verification for the exact event-filter form so this compiler regression cannot recur silently.
- No scripting/runtime architecture behavior changed from alpha.1.
- macOS CFBundleVersion: 16002.

## v0.16.0-alpha.1 - Shared scripting/orchestration foundation

- Added the dependency-free `JevMud.Scripting` project containing language-neutral runtime, host, permissions, scheduling, event, storage, clock, and structured execution contracts.
- Added capability-gated Client host adapters for semantic events, state, commands, mapper/pathfinding, Codex, namespaced script storage, timers, UI notifications, and structured logging.
- Added a managed bootstrap runtime so first-party behavior can exercise the scripting contracts before JevMUD chooses a public JavaScript/Lua runtime.
- Added owner-scoped execution and deterministic cancellation for automation workflows, mapper routes, Jev sessions, timers, and event subscriptions, with centralized task-fault reporting.
- Added provenance-aware command dispatch (`USER`, `AUTOMATION`, `JEV`, `MAPPER`, `SCRIPT`, `SYSTEM`) while retaining `ActionProcessor` as the only transport-facing command boundary.
- Added stable script event DTO projection, a state-reduction barrier, and bounded backpressured per-subscription delivery; script handlers see semantic events only after Core has reduced the corresponding event.
- Centralized human/automation command arbitration in the shared command adapter and bound script-host provenance to module identity, preventing authored scripts from impersonating USER/JEV/MAPPER/AUTOMATION sources.
- Added script/module-scoped persistence in a dedicated `script-state.db`; scripts do not receive raw mapper/Codex SQLite access.
- Migrated recurring automation timers to a compiled first-party runtime module; the visual Automation surface remains the user-facing authoring model.
- Migrated mapper route execution to shared command, scheduler, and owned-task primitives while graph, room identity, persistence, and pathfinding remain typed C# services.
- Migrated Jev command/timing/outcome execution to the same primitives while Jev remains a typed decision/policy layer.
- Added command provenance to transcript echo and durable command history plus runtime task/UI diagnostics.
- Added architectural regression coverage for permissions, owner cancellation, task faults, event ordering, storage namespaces, and provenance.
- Public user-script language selection and script-management UI remain intentionally deferred until these first-party migrations are proven.
- macOS CFBundleVersion: 16001.

## v0.15.0-alpha.23 - Mapper preference gate compile fix

- Restored the production `WorldKnowledgeStore` mapper recording/persistence preference gates required by the mapper ingestion switch.
- Recording now requires mapper services and automap; durable mapper writes additionally require `PersistKnowledge`.
- Static verification now requires both helper declarations so unresolved call-site-only regressions fail verification.

## v0.15.0-alpha.22 - Movement response correlation and mapper integrity

- Movement attempts are correlated to their outbound action id until the corresponding Avendar response completes.
- A movement response which produces no room observation expires that attempt instead of leaving stale direction state behind.
- Explicit movement failures remain authoritative and do not double-consume later queued movements.
- Batched movement commands retain response order, including mixed denied/successful movement.
- The state reducer and durable knowledge mapper both clear denied, unrecognized movement attempts.
- A later successful room observation can no longer be attached to an earlier denied direction.
- Added regression coverage for the Hall of Victors-style denial followed by a successful move.
- Updated macOS CFBundleVersion to 15022.

## v0.15.0-alpha.14

- Fixed the C# CS0136 compile failure in workflow `wait` handling caused by an `out` variable named `condition` extending across the method scope.

- Hardened workflow admission so manual and event-triggered starts share the configured concurrency ceiling; disabled automation/groups cannot be bypassed by the Automation workspace.
- One-shot workflows are now consumed only after an execution actually starts, avoiding false completion when a start is rejected by concurrency.
- `Continue` failure mode no longer emits a terminal `Failed` state before later reporting `Completed`; recovered step failures remain visible as running workflow activity.
- Human-override timing is stored atomically so workflow/background command threads cannot race the event reader while yielding to player input.
- Mapper-backed workflow navigation now executes the exact reachable route selected by `FindNearestAsync` instead of discarding it and immediately issuing a second route query.
- Mapper destination search now gives room, MOB/object/fixture, and item-source matches independent bounded candidate budgets before final relevance trimming, preventing broad room/area matches from starving semantic destinations.
- Added regression coverage for manual workflow concurrency and continue-on-failure lifecycle semantics.

## v0.15.0-alpha.11

- Completed the first-party automation/mapping baseline which will define the later extensibility contracts. No plugin API is introduced in this release.
- Added a deterministic workflow runtime with ordered steps, state/event waits, conditional branches, assertions, persistent variables, bounded retries, mapper-backed navigation, and explicit Jev escalation for unresolved combat/navigation/recovery decisions.
- Added event-triggered and state-triggered workflows, edge-triggered game rules, priorities, groups, cooldowns, one-shot execution, workflow cancellation, and bounded workflow concurrency.
- Automation command rate limiting now applies backpressure instead of silently dropping commands. Human commands create a configurable override window which deterministic automation yields to; queued trigger/rule/timer commands survive that window instead of being discarded.
- Added a persistent Automation workspace for running/cancelling workflows and inspecting variables and automation activity without rebuilding the main transcript UI.
- Expanded state expressions with boolean composition (including grouped negation), variables, event fields, room/entity/interactable predicates, ability/equipment predicates, vitals, inventory, and other modeled game state.
- Mapper search now resolves rooms, MOBs, fixtures/objects, and known item sources. `Nearest` and workflow `navigate <query>` choose the shortest reachable known destination rather than the first textual match.
- Route planning now supports avoided areas, terrain, MOB names, closed/blocked/unknown exits, avoided rooms, and preference for confirmed traversable edges. Directed/custom exits remain first-class route edges.
- Auto-move remains one confirmed edge at a time, supports pause/resume/step/stop, pauses on manual movement, can pause/resume around combat, and re-plans after deviations/timeouts without holding the navigator control lock during SQLite route searches. Cancelling a workflow navigation request also stops its active route.
- Navigation failures are learned into persistent exit state instead of deleting topology. Explicit closed-door failures become `Closed + Blocked` knowledge and are carried into route steps.
- Added optional automatic door opening with a configurable command template (`open {direction}` by default). It is disabled by default until the target MUD command syntax is explicitly configured/verified. Auto-move waits for semantic open confirmation before continuing.
- Mapper route previews now surface closed doors and unconfirmed exits.
- Consolidated mapper graph/search/route compatibility methods onto `MapperReadRepository` so first-party mapping has one route/search implementation rather than divergent writer/read-side algorithms.
- Added regression coverage for rich automation predicates, workflow persistence/execution/event context, bounded retry behavior, mapper constraints, closed-door failure learning, and mapper settings persistence.

## v0.15.0-alpha.10

- Completed the agreed baseline Telnet/OOB protocol pass: NAWS, GMCP, MSDP, MSSP, MCCP2, CHARSET, NEW-ENVIRON/MNES, MTTS/TTYPE, and EOR/GA prompt boundaries.
- Telnet negotiation is stateful and deduplicated so repeated WILL/DO offers do not create negotiation loops or duplicate activation frames.
- GMCP now performs Core.Hello/Core.Supports negotiation and exposes outbound GMCP through the transport.
- MSDP now preserves nested arrays/tables, discovers REPORTABLE_VARIABLES, subscribes only to supported JevMUD-relevant values, and exposes outbound MSDP.
- MSSP preserves repeated values instead of overwriting them.
- MTTS/TTYPE follows the client-name, terminal-type, capability-bitvector sequence and advertises MNES/TLS only when actually available.
- NEW-ENVIRON/MNES answers requested variables, escapes reserved octets, and exposes standard MUD client metadata.
- CHARSET negotiates UTF-8 and explicitly rejects unsupported translation tables.
- NAWS updates after live viewport changes rather than only at connection time.
- MCCP2 has bounded failure reporting and can relinquish decompression if the zlib stream ends cleanly without treating that condition as a socket disconnect.
- Telnet subnegotiation frames are bounded to 64 KiB; malformed/oversized frames emit protocol errors instead of growing without limit.
- Added protocol lifecycle/error events plus EOR/GA prompt-boundary events for the later automation engine.
- Added an EOR setting to Settings → Protocols; protocol settings apply on the next connection.
- Expanded protocol regression coverage for negotiation deduplication, MTTS sequencing/reset, UTF-8 CHARSET, NEW-ENVIRON filtering, nested MSDP, discovery-driven MSDP reports, bounded malformed frames, prompt boundaries, and settings persistence.

## v0.15.0-alpha.9

- Codex MOBs aggregate by mapped area instead of by individual room. Same-named MOBs in different areas remain distinct; unknown-area observations stay room-scoped until classified.
- Loot provenance links observed items to MOBs and their locations. Items acquired from corpses or explicit MOB drops can be routed back to their MOB source, and MOB entries expose observed loot.
- Added a golden combat-loot regression fixture from a real Avendar kill burst: death, XP, corpse loot, currency loot, and corpse destruction are parsed as distinct semantic events.
- `You quickly destroy the corpse of <mob>.` now emits `CorpseDestroyed` and removes a matching modeled corpse so automation cannot act on stale corpse state.

- Fixed GUI compilation after the mapper/Codex workspace re-architecture: `CodexWorkspace` now imports the Avalonia namespace that owns `Thickness`.
- Centralized auto-move status formatting in `AutoMoveDisplay` so `MainWindow` and `MapperWorkspace` share one formatter instead of retaining a removed `MainWindow` helper.

## v0.15.0-alpha.6

- Re-architected Mapper and Codex as persistent, lifecycle-owned workspaces instead of transient panels built by `MainWindow.RenderDock()`.
- Codex now owns one long-lived virtualized result list, selection, scroll position, search state, detail pane, and cancellation scope. Live game/render ticks never rebuild its visual tree; knowledge ingestion only marks newer data as available until an explicit refresh/search.
- Mapper now owns one long-lived immediate-mode viewport with pan, zoom, hit-testing, destination selection, route controls, and auto-move controls. `MainWindow` only attaches/detaches the workspace and supplies cheap live-state updates.
- Added `MapperReadRepository`, an isolated read-only SQLite path using private cache, `query_only`, bounded command timeouts, bounded neighborhood projection, bounded search, and incremental route traversal. Mapper reads never run schema initialization and never share the live writer cache.
- Mapper graph acquisition, layout, search, route planning, Codex searches, Codex detail reads, and mapper metadata reads/writes are all kept off the Avalonia UI thread.
- Mapper opening has a bounded background load timeout and can fail recoverably without blocking the rest of the client.
- Added structural anti-regression checks preventing Mapper/Codex rendering logic from drifting back into `MainWindow` or calling `RenderDock()` internally.
- Added regression coverage for the isolated mapper reader's neighborhood, MOB search, and route behavior.

## v0.15.0-alpha.5

- Attempted to isolate Codex live refreshes and mapper rendering by retaining the Codex result list and moving the map to a custom-drawn viewport.
- Added bounded mapper graph acquisition and Codex detail reads off the UI thread. These changes reduced individual hot paths but did not fully remove the explorer lifecycle coupling later replaced in `0.15.0-alpha.6`.

## v0.15.0-alpha.4

- Fixed mapper beach-ball by moving SQLite graph/route work off the UI thread and bounding the visual viewport.
- Fixed fixture/object entries appearing as MOBs in Codex and mapper search, including a narrow startup repair for legacy persisted fixture rows.
- Added configurable command separator batching (`;` by default, blank disables, backslash escapes), with ordered mapper movement inference for batched navigation.

## v0.15.0-alpha.3

- Fixed Mapper launch freezes by rendering a bounded 48-room neighborhood, compacting graph coordinates before creating the Avalonia canvas, and expanding the visible neighborhood explicitly instead of materializing the configured maximum immediately.
- Fixed Codex launch freezes by making the initial overview query-free. Category browsing is lazy, cross-category search starts after two characters, and expensive entity/combat browse aggregation is bounded to recent indexed windows.
- Rebuilt Mapper as a visual, searchable world explorer. Known rooms render as a connected graph; current room, planned route, destination, avoided rooms, and unusual exits are visually distinguishable.
- Mapper search resolves both rooms and remembered MOBs/entities into route destinations. Controlled auto-move provides Go, Pause, Resume, Step, and Stop and advances only after confirmed room arrival.
- Auto-move pauses on combat by default, can resume after combat, re-plans boundedly after deviations/failures, and fails visibly when arrival confirmation times out rather than blindly flooding movement commands.
- Rebuilt Codex as a category/search browser for rooms, MOBs, items, abilities, and combat history with details, observation history, known locations, raw references, and direct routing where location data exists.
- Map and Codex open as wider explorer workspaces while preserving the normal contextual dock width and avoiding destructive re-renders on unrelated state updates.
- Added the Jev master switch to the main toolbar, command palette, and Settings. Disabling Jev stops Jev decisions/execution without modifying the configured per-domain authority profile.
- Expanded Mapper settings with visual graph bounds and explicit auto-move controls for combat behavior, step pacing, arrival timeout, and bounded re-planning.
- Added durable map/entity search, graph projection, Codex query/detail APIs, and regression coverage for the new knowledge workflows.

- Replaced the debounce-and-cancel Jev loop with a sequential autonomy supervisor. Semantic triggers are coalesced, evaluated one batch at a time, and synchronized to the authoritative reducer sequence before Jev sees state.
- Only one autonomous command may be in flight. Navigation waits for a room observation/failure, recovery waits for posture/resource feedback, and combat waits for a combat/prompt outcome before another autonomous command can dispatch.
- Recovery no longer guesses that every non-`standing` posture needs `stand`. Only observed resting/sitting/sleeping posture can offer `stand`, and Avendar's `You are already standing.` feedback repairs posture state immediately.
- Prompt telemetry drives Recovery only. Navigation resumes from complete room observations or exactly once when an active recovery phase returns to ready/standing state, preventing prompts from acting as a navigation metronome.
- Navigation now has deterministic loop protection: immediate reversal is removed when another traversable exit exists, recent-room routes are deprioritized, and backtracking remains available at genuine dead ends.
- The Jev control event feed is lossless; autonomy no longer relies on a bounded observer which could drop action/outcome events.
- Recent room communications are added to bounded Jev context. Occupants who recently spoke are excluded from blind Auto grind target acquisition, reducing accidental attacks on social/quest NPCs.
- Bot/Autonomous presets only grant Auto to domains with executable, revalidated projectors (Combat, Recovery, Navigation). Unsupported domains remain observation-only instead of advertising autonomy they cannot execute.
- Added regression coverage for repeated `stand` suppression, posture repair, unknown-posture safety, immediate reverse-route suppression, dead-end backtracking, and social-speaker target protection.
- Auto actions are sequenced by observed outcomes rather than a fixed post-decision debounce, so a fast prompt cannot be consumed by a cooldown and strand recovery/navigation. A bounded outcome watchdog releases a missing response without blindly repeating the same action.
- Recent local speakers are protected for target acquisition, including the common Avendar case where the room lists a descriptive NPC but dialogue reveals the proper name moments later; a sole unresolved occupant is conservatively treated as the speaker.

## v0.13.0-alpha

- Prompt slurp is a display-only preference: `[J|…]` telemetry can be hidden from the transcript without removing it from parsing or raw logs.
- Jev now has executable Combat, Recovery, and Navigation domains which cooperate as an Auto grind loop; idle target acquisition yields to deterministic recovery thresholds, and declining a target can continue into route selection.
- Dispatched Human/Jev/Rules/Hybrid commands use bright source-aware local echo in the transcript.
- The contextual dock fully releases its grid column when closed, so the MUD surface consumes the reclaimed width.
- World/Codex routes display known room names rather than internal `avendar:…` identities.
- The macOS application menu defines `About JevMUD…` and opens JevMUD's own About window rather than Avalonia's default.
- The in-window header is reduced to one compact context/control rail: room context and character identity on the left, navigation/utilities on the right.
- MUD commands are locally echoed into the game transcript. Human commands appear as `> command`; automated commands are explicitly marked as `[Jev] >`, `[Rule] >`, or `[Hybrid] >`. Sensitive input is never echoed.
- Action execution source is preserved in events and durable action history, giving Jev bounded context over recent human and automated commands rather than only manually-entered history.
- Avendar ability help is normalized from terminal hard-wrapped lines into contiguous semantic prose before storage and presentation. Existing stored help is normalized on read as well.
- Combat Jev context now explicitly includes stored help for every currently available Jev-capable ability, including syntax, activation lag/cost, descriptive behavior, and additional parsed fields.
- Known equipped-item context remains bounded but now includes up to twelve equipped references for richer combat decisions.

## v0.11.2-alpha

- Item identification captures Avendar `Affects <stat> by <amount>` modifiers as structured knowledge.
- Equipment hover promotes item modifiers such as `HP +3`, `MV +20`, and `CON +1` ahead of secondary metadata and persists them through the Codex.

macOS-first C#/.NET MUD client for Avendar with an event-driven game-state core and optional Jev/TypeSafe decision support.

The GUI is now the primary client. The Terminal.Gui client remains as a fallback/debug surface.

## Product direction

NexMUD should feel like a game client first, not an observability dashboard. The primary interaction model combines a text-first MUD transcript with the compact, glanceable information hierarchy of game UIs such as classic Ultima-style clients and modern ARPG HUDs. The GUI also follows patterns proven by long-lived MUD clients such as Mudlet, zMUD/CMUD, and TinyFugue:

- the game transcript is the dominant surface;
- decoded server output is append-only from the client's perspective: parsers may extract state, but must never delete or rewrite server text;
- the command line is always immediately available;
- vitals, room context, abilities, automation, and diagnostics live in secondary surfaces;
- ANSI color, bold, italics, underline, background color, and reverse-video presentation are preserved or enhanced where the GUI can render them;
- command history and fast movement controls stay close to play;
- scrolling back automatically opens a Mudlet-style split view so new live output remains visible;
- session logging and user-configurable highlights are first-class conveniences;
- diagnostics exist without taking over the play experience.

No source code from those clients is copied. NexMUD reuses the interaction patterns which have proven useful for MUD play.

### GUI design language

The desktop client uses one shared `UiTheme` rather than per-window palettes. The visual hierarchy is intentionally restrained:

- the transcript remains the dominant canvas; secondary information must earn its screen area;
- sidebar content uses headings, rules, aligned metrics, and compact tables instead of cards inside cards;
- semantic color is reserved for state, selection, health/resource identity, warnings, and actionable Jev status; ordinary chrome stays neutral;
- the Avalonia Fluent theme runs in compact density and inherits NexMUD's restrained steel-blue accent rather than a separate per-window palette;
- character, World, Abilities, Codex, and Jev surfaces use aligned metrics, compact tables, and bounded previews so the sidebar behaves like an information instrument rather than a form;
- equipment uses a two-column dense table while preserving every observed slot; hovering an equipped item shows a Diablo-style structured identification tooltip, sourced from live state or the persistent Codex;
- progressive disclosure is used only for genuinely secondary/long material. Disclosure state and sidebar scroll position survive live state re-renders, so panels do not collapse or jump merely because new game state arrived;
- settings use the same typography, spacing, fields, buttons, dividers, and palette as the game window;
- ambiguous tiny icon-only controls are avoided for primary utilities. Settings is explicitly labeled.

The spacing system is intentionally compact and consistent, with square-to-subtle-radius controls rather than rounded/bubbly containers.

## GUI

Run the GUI:

```bash
dotnet run --project src/JevMud.Gui/JevMud.Gui.csproj -- --host avendar.net --port 9999
```

Use the saved connection without explicitly passing host/port:

```bash
dotnet run --project src/JevMud.Gui/JevMud.Gui.csproj
```

Prevent startup auto-connect:

```bash
dotnet run --project src/JevMud.Gui/JevMud.Gui.csproj -- --no-connect
```

The GUI provides:

- a large selectable ANSI-aware game transcript which presents server output losslessly by default and locally echoes dispatched commands in bright source-aware colors; Jev/rule-driven commands are visibly labeled and sensitive input is never echoed; optional prompt slurp can hide only Avendar telemetry frames from the presentation while raw logging and state extraction still receive them;
- automatic split-view scrollback: the history pane stays where you left it while a resizable live-output pane continues at the bottom;
- persistent Mudlet-style command input embedded directly in the MUD console, with cross-session Up/Down history, contextual Tab/Shift+Tab completion, and overwrite-on-next-command selection;
- password masking and exclusion of passwords from command history/events;
- an ARPG-style HUD for HP, mana, movement, location, XP, and combat context;
- a dismissible, deliberately flat reference dock with world/combat, character/loadout, abilities, Jev, and Codex views;
- a compact World intelligence surface with room/environment metadata, exit controls, content counts, dense entity rows, and durable room-memory/routes instead of sparse nested panels;
- room-entity actions such as look, consider, read, examine, drink, and sacrifice when supported by parsed traits;
- complete observed equipment slots, with equipped items directly identifiable from the character view; hover reveals structured `id` data, or `No data. id this item` when no reference has been captured yet;
- one consolidated Abilities reference instead of separate top-level Skills/Spells chrome; all observed skills/spells are shown in one compact capability matrix with proficiency or unlock level, and hover exposes normalized stored server help when available;
- a Codex information dashboard which prioritizes knowledge-base coverage, current-room memory/routes, recurring observations, recent references, and activity relevant to Jev rather than displaying raw database-oriented rows;
- a dedicated Settings window for interface, connection, transcript highlights, Telnet identity, and Jev authority;
- literal or regex highlight rules with foreground, bold, underline, case-sensitivity, and enable/disable controls;
- session logging as ANSI-preserving text, presentation-only plain text, or JSON Lines;
- aliases with `$*` and `$1`-`$9` argument expansion;
- configurable command key bindings/macros using `Primary`, Command/Meta, Ctrl, Alt/Option, Shift, and F-keys;
- literal or regex triggers which dispatch commands without gagging or rewriting server output;
- repeating or one-shot command timers;
- transcript search (`Command/Ctrl+F`), return-to-live (`Command/Ctrl+G`), logging toggle (`Command/Ctrl+L`), and a keyboard-first command palette (`Command/Ctrl+K` or `Command/Ctrl+Shift+P`);
- resizable context dock plus persisted window, dock, active-drawer, and live-split geometry;
- a structured Jev decision inspector rather than a chat surface, plus a compact current-decision readout in the combat HUD;
- a persistent SQLite Codex which records sessions, raw/semantic events, rooms/routes/entities, character observations, abilities, equipment, structured item identifications, skill/spell help reference, combat history, communications, executed non-sensitive command history with Human/Jev/Rules/Hybrid source, and Jev decisions;
- no permanent debug/events panel in the primary play surface.

## Ability catalog

Skills and spells are first-class character state.

The client distinguishes:

- existence in the catalog;
- required level;
- available vs unavailable (`n/a` is not zero proficiency);
- proficiency when Avendar reports one;
- fresh vs stale observations;
- coarse ability domain where semantics are explicitly known;
- action eligibility, which remains separate from catalog availability.

`skills` collection spans Avendar's `[Hit Return to continue]` pager. A blank Enter continues the same capture rather than starting a new response. The final prompt closes the capture and produces one complete snapshot. If another nonblank command interrupts collection, the result is partial and is merged without deleting previously known skills.

`spells` has an independent collector. `No spells found.` is modeled as an authoritative complete empty spell catalog. Structured spell rows are parsed conservatively when they use the same observed level/percentage form; unknown spell-list formats are left unmodeled rather than guessed.

The active response owner (`Score`, `Skills`, or `Spells`) is modeled in session state so UI/orchestration can distinguish a pager belonging to an ability capture from unrelated modal output.

Not all skills are combat skills. Only explicitly understood skills receive a domain. Unknown skills remain `Unknown` until their semantics are documented.

### Authoritative item and ability reference

`id <item>` responses are captured structurally without suppressing their server output. The common Avendar identification form records object name, flags, weight, wear locations, level, material, item type, weapon type/flags, damage type, damage dice/average, and any additional colon-delimited fields the server supplies. Unknown extended identification fields are retained rather than discarded, persisted in the Codex read model, surfaced in item hover reference, and available to bounded Jev context, so richer identification available to specialized characters can be learned without a separate schema migration for every field.

`help <ability>` responses are likewise captured as reference documents. The parser records whether the entry is a skill or spell, activation lag, mana cost, syntax, descriptive text, additional fields, and the raw server help block. Ability rows in the GUI request this help directly, and the structured reference is persisted in the Codex.

The client does not run a second hidden game connection to harvest these references. Requests use the player's normal connection and their output remains visible in the transcript.

## Jev integration

Jev is integrated as a **System One decision model**, not as a chat LLM. NexMUD sends explicit program state plus typed questions to TypeSafe's `/v1/systemone` endpoint and consumes typed probabilistic answers. There is no free-form completion or text-generation step in the control path.

The combat workflow currently decomposes one state snapshot into parallel questions in the same Jev request:

- **Choice** - choose one currently legal intervention from a closed action set and return the full action probability distribution plus confidence;
- **Score** - place immediate combat danger on an ordered rubric and return a fractional score, distribution, and confidence;
- **Noul** - return `P(true)` for whether survival/disengagement should dominate offensive pressure.

The surrounding C# remains authoritative. It constructs the legal action set, computes deterministic eligibility, owns execution policy, applies the configured authority level, revalidates current game state before execution, and serializes commands through the normal action queue. Jev chooses stable semantic action IDs, not raw MUD command strings; only the deterministic Avendar adapter materializes an approved decision into a command. Jev never owns the socket, mutates state, invents an out-of-schema command, or bypasses validation.

Automatic Jev evaluation is event-driven from semantic state changes rather than raw text lines. Combat changes drive Combat; prompt/resource changes drive Recovery; complete room/route observations drive target acquisition and Navigation. A sequential autonomy supervisor coalesces semantic triggers, waits until the authoritative state reducer has processed the triggering event sequence, and permits only one autonomous command in flight. Prompt telemetry therefore cannot repeatedly retrigger movement or posture commands while a prior action is still settling. The authority matrix is operational:

- **Off** - no Jev evaluation for the domain;
- **Observe** - evaluate and retain diagnostics without presenting an actionable recommendation;
- **Suggest** - surface the typed decision without executing it;
- **Approve** - place the selected action in an explicit approval flow, then revalidate before sending;
- **Auto** - revalidate the selected action against current state, then queue it through the normal action processor.

The Jev drawer presents the model as a decision workflow: selected Choice, action distribution, Choice confidence, parallel Score/Noul evaluations and their distributions, model/state version, request latency, token usage, authority/source, approval state, and recent decision history. It deliberately does not present a chatbot transcript or fabricate a natural-language rationale.

The executable Jev projectors are deliberately narrow:

- **Combat** runs during active combat and, while idle and sufficiently recovered, may choose from currently observed room occupants for target acquisition. Active combat always exposes `continue` plus only modeled abilities whose deterministic eligibility is `Eligible`; `Unknown` and `Ineligible` actions are excluded.
- **Recovery** owns depleted idle state and chooses among hold, rest, the modeled `recover` ability, and `stand` only when the observed posture is one known to require standing (resting/sitting/sleeping). Server feedback such as `You are already standing.` repairs posture state so the same command cannot loop on stale state.
- **Navigation** runs only while idle, standing, sufficiently recovered, and with known exits. Its closed choices contain only currently traversable directions plus `stay`, with mapped destination names supplied when known. The supervisor removes immediate reverse movement when alternatives exist, deprioritizes recently visited destinations, waits for movement outcome before another route decision, and still permits backtracking from a real dead end.
- All three receive the same compact live projection plus bounded Codex context, including authoritative captured ability help and identified equipment facts.
- Every selected semantic action is materialized and revalidated against the latest state immediately before dispatch.

In Auto authority the implemented domains form one priority-ordered grind loop: acquire/continue combat, recover resources, then navigate. Declining an idle combat target passes through to recovery/navigation instead of stranding the loop. Recent speakers are treated as protected from blind grind target acquisition, while their communications are included in bounded Jev context. Inventory, Loot, Quests, Social, and Training remain observation-only in the Bot/Autonomous presets until executable projectors exist.

Jev requests can also receive bounded durable context from the local Codex: the current room's historical routes/entities, prior encounters with the current target including bounded recent structured combat events, identified equipped-item facts, authoritative skill/spell help references, aggregate memory counts, and recent executed commands with Human/Jev/Rules/Hybrid source. This projection is deliberately compact: long room text/commands are bounded and local filesystem/database paths are never sent to Jev. Live `StateSnapshot` data remains authoritative; persistent memory is supporting context and never overrides current observed state.

Set the API key before launch:

```bash
export TYPESAFE_API_KEY='...'
```

Default model: `jev-latest`.

Override it with:

```bash
dotnet run --project src/JevMud.Gui/JevMud.Gui.csproj -- --jev-model jev-1.13.0
```

## Convenience automation

NexMUD's traditional-client automation is intentionally separate from Jev. These features are deterministic, user-authored `Rules` actions and still pass through the shared ordered action queue.

- **Aliases** expand the first command token, with `$*` for the entire argument tail and `$1`-`$9` for positional arguments. Login/password/editor input is never alias-expanded.
- **Triggers** match complete presentation-text lines using literal or bounded-time regex rules. Regex captures may be substituted into commands with `$1`-`$9`. Triggers never gag or alter the transcript.
- **Timers** can dispatch repeating or one-shot commands while connected and in normal input mode.
- **Highlights** remain presentation-only and preserve the underlying server text and ANSI state.
- **Search** works over the accumulated session transcript and never mutates it.
- **Logging** supports ANSI-preserving text, plain presentation text, and JSON Lines containing timestamp/source/text.
- **Command history** is persisted locally across client launches while password input is never recorded.
- **Tab completion** uses current exits, room target keywords, known abilities, aliases, and common MUD commands; Shift+Tab cycles backward.
- **Key bindings** map user-configured gestures to commands without bypassing the normal ordered action path.

The automation service consumes its own lossless event subscription so UI rendering, semantic parsing, logging, and convenience automation remain isolated consumers of the same server stream.

## Persistent settings

Connection settings, GUI/workspace preferences, highlights, aliases, triggers, timers, command key bindings, logging format, neutral Telnet terminal identity, Jev model, and the complete Jev authority matrix are persisted as JSON. GUI saves, TUI saves, and local `:jev` commands use the same shared settings store.

The default settings path is the platform application-data directory under `NexMUD/settings.json` (with one-time migration from the legacy `JevMUD` directory). The default server-facing terminal type is `xterm-256color`; the client does not advertise its Jev integration to MUD servers.

Writes are serialized and atomic through a temporary file replacement. Explicit UI saves persist before reporting success; the event-driven persistence worker remains a fallback for authority changes originating elsewhere.

## Persistent Codex / world memory

`JevMud.Client.Knowledge.WorldKnowledgeStore` maintains a local SQLite database at the platform application-data path under `NexMUD/knowledge.db`. It is a durable observation store, not a second authoritative reducer.

The database keeps both a generic append-only event archive and query-oriented projections for sessions, rooms and directed exits, recurring room entities, character observations, skills/spells, equipment, item identifications, ability-help documents, combat events, communications, non-sensitive executed command history with Human/Jev/Rules/Hybrid source, and Jev decisions. Event identity is GUID-based so event sequence numbers may restart on a later client launch without colliding with prior sessions.

The Codex drawer exposes useful portions of this memory for reference. Jev receives only bounded context relevant to the current decision rather than an unbounded transcript dump. Passwords remain redacted/excluded by the existing input and action-observability rules. Failure of the knowledge database is non-fatal: play and live Jev decisions continue from current state.

## Avendar state extraction

Use the compact machine prompt:

```text
prompt [J|%h/%H|%m/%M|%v/%V|%x|%X|%s|%r|%e|%T|%d]
```

The prompt is parsed as a stream frame for state extraction. Semantic parsing cannot suppress it: raw server text still reaches the event stream and raw logging unchanged. An explicit **Prompt slurp** presentation preference may hide only `[J|…]` telemetry frames from the visible transcript after parsing; it does not alter source events, logging, or state extraction.

Modeled state includes:

- HP, mana, movement;
- experience and experience-to-level;
- position;
- room name, terrain, light, and exits;
- structural/open/closed/blocked exit state;
- room identity independent of display name;
- directed observed topology;
- occupants, objects, corpses, and room interactables;
- room-content completeness/freshness;
- score/profile/stat/inventory data;
- multi-denomination currency;
- equipment, including duplicate/empty observed slots and identified equipped-item facts;
- structured `id` item reference;
- structured skill/spell `help` reference;
- general character conditions;
- skills and spells;
- combat lifecycle, attack outcomes, damage language, and target condition;
- input modes including login, password, pager, and editor.

Room parsing preserves interactables such as signs, boards, plaques, notes, bins, fountains, and readable volumes. Duplicate occupants remain distinct observations. Tells, says, OOC messages, tips, combat output, and other semantic lines are claimed before room-content classification.

## Architecture

```text
TCP/Telnet
    -> decoded server text -> lossless display/log subscribers
    -> ANSI/text stream framing -> Avendar semantic adapters
    -> ordered event stream
       -> durable SQLite Codex (lossless side consumer)
    -> single-writer reducer
    -> immutable/versioned snapshots
    -> GUI / TUI / Jev consumers

human / deterministic Rules / Jev action
    -> shared action queue
    -> Jev authority + current-state validation where applicable
    -> transport
```

Important constraints:

- raw display text does not become authoritative game state;
- server display text is lossless and append-only; semantic extraction cannot suppress it;
- the UI never owns game truth;
- prompt telemetry updates state; resource/position changes can trigger Recovery and Navigation, while prompt frames never directly trigger Combat;
- slow UI/Jev consumers do not block parsing/state;
- human play remains functional when Jev or the durable knowledge database is unavailable;
- passwords are redacted before observability;
- partial ability captures never erase previously known catalog entries.

## TUI fallback

```bash
dotnet run --project src/JevMud.Tui/JevMud.Tui.csproj -- --host avendar.net --port 9999
```

The TUI remains useful for debugging and low-overhead play, but new UX work should target `JevMud.Gui` unless a feature belongs in shared runtime/state.

## Local commands

```text
:connect <host> <port> [tls]
:disconnect
:jev preset <off|copilot|bot|autonomous>
:jev <domain> <off|observe|suggest|approve|auto>
:jev status
:avendar prompt
:help
:quit
```

GUI shortcuts:

```text
Command/Ctrl+F         search transcript
Command/Ctrl+G         return to live output
Command/Ctrl+L         toggle session logging
Command/Ctrl+K         command palette
Command/Ctrl+Shift+P   command palette
Tab / Shift+Tab        contextual completion
```

Plain input is sent to the MUD. Blank input sends Enter, including pager continuation. Aliases are applied only to normal game commands, never login/password/editor input.

## Verification

```bash
./scripts/verify.sh
```

The verification script runs structural checks, restore, build, and the behavior-test executable.

This source artifact was produced in an environment without a .NET SDK, so the included code was statically verified here but still requires `./scripts/verify.sh` on a machine with .NET 10 before treating the build as green.

## macOS app bundle

For normal macOS app identity (NexMUD in the menu bar/Dock plus the bundled N icon), run the real application bundle. Application shutdown is asynchronous and bounded: Command-Q no longer synchronously waits for sockets, background workers, settings, or SQLite on the Avalonia UI thread.


```bash
./scripts/run-macos-app.sh
```

Arguments can be forwarded to the app:

```bash
./scripts/run-macos-app.sh --host avendar.net --port 9999
```

The lower-level build command remains available:

```bash
./scripts/build-macos-app.sh
open artifacts/macos/NexMUD.app
```

`dotnet run` is still useful for compile/debug loops, but it launches the raw .NET executable rather than through macOS LaunchServices. The `.app` path is the supported way to verify the product title, menu-bar identity, Dock identity, and `.icns` icon.

The TypeSafe API key can be entered under **NexMUD > Settings > Jev**. On macOS it is stored in the login Keychain, not in `settings.json`. `TYPESAFE_API_KEY` remains supported and takes precedence for the current process.

The `equipment`/`eq` response is captured as a complete slot snapshot, including duplicate ring/neck/wrist slots, empty slots, dual wield, and character brands. The Character panel renders that snapshot as the current loadout.
