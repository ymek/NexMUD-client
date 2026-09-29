# NexMUD Avalonia World-First UI Architecture

**Status:** Implemented architecture contract  
**Release line:** 0.20.x  
**Date:** 2026-09-27

## 1. Product interaction model

NexMUD is a single-window game client. The World transcript is the persistent application surface; secondary capabilities coexist with it rather than replacing it or opening independent top-level windows by default.

The permanent gameplay regions are:

1. World transcript.
2. Command input.
3. Character/gameplay status in the default World state.
4. Room/context intelligence in the default World state.

Top navigation changes the **right-side workspace**. It does not replace World. Character is not a separate top-level workspace: its gameplay information is consolidated into the persistent World rail.

Detached OS windows are not part of the normal navigation model. Focused editors and short-lived dialogs may still exist where they represent a bounded edit/confirmation operation, but Codex, Mapper, Automation, Scripting, Abilities, Jev, transcript search, and Settings are in-app surfaces.

## 2. Shell ownership

`MainWindow` owns only application composition and interaction routing:

- persistent app bar;
- persistent World surface;
- persistent command bar;
- right-side workspace host;
- settings overlay host;
- workspace activation/deactivation;
- main-window focus policy;
- top-level resize policy.

Domain behavior remains outside Avalonia. Transport, protocol parsing, state reduction, knowledge persistence, mapper graph/pathfinding, Codex search, automation execution, scripting/Jint execution, Jev decision policy, and command arbitration retain their existing owners.

## 3. Presentation boundaries

### `GameplayShellViewModel`

Immutable projection of the current `StateSnapshot` plus durable room knowledge and metadata. It has no Avalonia dependency and performs no I/O.

### `CharacterHudViewModel`

Owns presentation-ready:

- character name;
- race/class/level identity;
- XP and level progression;
- HP, MA, MV;
- position;
- combat state;
- target;
- Jev state;
- STR, DEX, CON, INT, WIS, CHA;
- current conditions/effects;
- inventory item/weight capacity;
- equipment summary/details.

### `RoomContextViewModel`

Owns presentation-ready:

- room name;
- area/zone;
- terrain/environment/light;
- exits and exit state;
- people/MOBs;
- objects;
- fixtures;
- corpses;
- recent observations;
- visit count;
- known routes/connections;
- recurring entities;
- last observation time.

Persistent gameplay panels consume only these immutable view models and injected user-intent callbacks.

## 4. Primary layout

The main application uses a three-column body:

```text
[ WORLD ][ splitter ][ RIGHT WORKSPACE ]
```

The World column contains:

```text
[ transcript / optional live-tail split ]
[ foveal gameplay HUD                  ]
[ command input                        ]
```

World never leaves the visual tree when navigating NexMUD.

The right workspace swaps among:

- default gameplay rail;
- Abilities;
- Codex;
- Mapper;
- Automation;
- Scripting;
- Jev inspector;
- transcript search.

Settings is a separate in-app overlay above the body so World remains perceptually present behind it.

## 5. Required application states

### 5.1 Default World

**Layout**

```text
[ WORLD ~68–75% ][ CHARACTER / ROOM ~320–420 px ]
```

**Persistent regions**

- app bar;
- transcript;
- command input;
- Character HUD;
- Room / Context.

**Right hierarchy**

1. Compact Character identity pane, expandable in place.
2. Room / Context, which receives the remaining vertical space and is the primary exploration surface.

**Scrolling**

- transcript scroll owns transcript history only;
- expanded Character detail scrolls within a bounded height;
- Room / Context scrolls independently and retains pinned quick actions;
- command input never scrolls away.

**Resize**

- gameplay rail remains approximately 340–420 px;
- World absorbs remaining width;
- below the minimum usable width, the window stops shrinking rather than collapsing World.

**Minimum usable dimensions**

- 1180 × 720.

### 5.2 Codex open

**Layout**

```text
[ WORLD ~56–60% ][ CODEX ~40–44% ]
```

Character/Room rail is replaced by Codex while Codex is active. It returns intact when World is selected.

**Codex hierarchy**

1. search/filter controls;
2. category/navigation surface;
3. entity list;
4. detail/related information.

**Scrolling**

World and Codex maintain independent scroll state.

**Resize**

Both columns use star sizing so Codex receives useful working space instead of a fixed narrow rail.

**Minimum usable dimensions**

- 1180 × 720; Codex should retain at least ~430 px.

### 5.3 Mapper open

**Layout**

```text
[ WORLD ~48–52% ][ MAPPER ~48–52% ]
```

**Hierarchy**

Mapper keeps its own search, viewport, route state, and route controls. World remains visible during movement and route execution.

**Scrolling / input**

- map viewport owns pan/zoom;
- transcript remains independently scrollable;
- command input remains active and attached to World.

**Resize**

The map receives roughly half the body and must not degrade into a small context widget.

**Minimum usable dimensions**

- 1260 × 720 preferred; 1180 × 720 hard shell minimum.

### 5.4 Automation / Scripting open

**Automation layout**

```text
[ WORLD ~47% ][ AUTOMATION ~53% ]
```

**Scripting layout**

```text
[ WORLD ~44% ][ SCRIPTING ~56% ]
```

Automation remains a user-facing behavior workspace with rules/workflows/activity. Scripting is a focused development workspace with scripts, runtime state, diagnostics, logs, permissions/configuration as those capabilities become available.

Neither workspace turns the full application into an IDE: World and command input remain continuously available.

**Minimum usable dimensions**

- 1260 × 760 preferred; 1180 × 720 hard shell minimum.

### 5.5 Settings open

Settings is an in-app modal sheet/overlay, not an OS window.

```text
+---------------------------------------------------------+
| dimmed World + current workspace remain visible        |
|        +--------------------------------------+         |
|        | SETTINGS                             |         |
|        | category nav | settings page         |         |
|        |              |                       |         |
|        | Cancel                    Save       |         |
|        +--------------------------------------+         |
+---------------------------------------------------------+
```

**Persistence**

World stays mounted behind the scrim. The current tool workspace is not destroyed.

**Scrolling**

Settings owns its page scrolling. Background interaction is blocked while the sheet is open.

**Resize**

The sheet uses approximately 78–88% of available width and 82–90% of body height with bounded maximum dimensions.

**Minimum usable dimensions**

- main shell minimum 1180 × 720; settings content itself should remain usable at ~820 × 600.

## 6. Workspace lifecycle

Workspaces which own cancellable activity remain long-lived controls rather than being rebuilt on each state tick.

### Mapper

`MapperWorkspace` owns graph loads, destination search, route state, viewport state, and mapper cancellation. `Activate()` begins/refreshes presentation work. `Deactivate()` cancels presentation-only work without stopping domain services unnecessarily.

### Codex

`CodexWorkspace` owns search, selected entity, detail loading, and Codex-specific cancellation. It is active only while selected in the right workspace.

### Automation

`AutomationWorkspace` owns automation inspection/activity presentation. Automation runtime execution is independent from visibility.

### Scripting

`ScriptingWorkspace` owns scripting/runtime presentation. Jint/runtime execution is independent from visibility.

### Character / Abilities / Jev / Search

Character is a state-derived persistent World-rail control, not a workspace destination. Its compact identity header remains visible and its details expand/collapse in place from the same immutable character projection used by the gameplay HUD.

Abilities, Jev, and Search remain state-derived internal workspace controls hosted in a `ContentControl`. They are rebuilt/refreshed from the latest immutable state when visible.

## 7. Workspace switching

The shell tracks one active right-side workspace.

Switching performs:

1. deactivate prior workspace presentation lifecycle;
2. select the new control in the shared workspace host;
3. apply that workspace's width ratio;
4. activate/refresh the selected workspace;
5. update navigation treatment;
6. preserve transcript and command state.

Selecting World restores the persistent gameplay rail and fixed rail-width policy.

No normal navigation action calls `Window.Show`, `ShowDialog`, or constructs a new top-level tool window.

## 8. Focus policy

- World navigation focuses command input.
- Opening Codex/Mapper/Automation/Scripting does not move keyboard ownership away from command input unless the player explicitly focuses the workspace.
- Search may explicitly focus its search field.
- Settings traps interaction inside the in-app sheet until saved/cancelled.
- Passive combat, room, Jev, automation, or mapper state changes never switch the active workspace.

## 9. Character gameplay architecture

Character state has two projections from one canonical `CharacterHudViewModel`:

1. the foveal HUD directly beneath the transcript: level, exact XP values, HP, MA, MV, position, combat, target, and Jev;
2. the persistent right-rail Character pane: name, lineage/class/level, alignment, and an explicit expand/collapse affordance.

When expanded, the Character pane adds the six attributes, hitroll/damroll/saves/AC, exploration/wealth, item/weight capacity, conditions/effects, and equipment summary/details. Expanded detail is height-bounded and internally scrollable so it cannot consume the Room surface.

No separate Character top-navigation item or Character workspace exists. No fake/generated portrait is displayed.

## 10. Room / Context visual architecture

Order:

1. current canonical room name;
2. compact area/terrain/light metadata;
3. exits as immediate directional controls;
4. only populated visible-entity groups, with actual entity names and compact counts;
5. recent observations;
6. compact durable room memory;
7. pinned quick actions.

The canonical current-room name takes precedence over mapper metadata labels. Empty People/Objects/Fixtures/Corpses groups do not receive full cards. The panel is text-first and contains no generated room imagery.

## 11. Visual system

Semantic palette:

- Obsidian `#0B0D10` / near-black transcript foundation;
- Midnight `#0F1B2A`;
- Slate `#1E293B`;
- Parchment `#E6DDC6`;
- muted gray `#9CA3AF`;
- Brass `#D4A85B`;
- Cyan `#22D4BF`;
- Success `#22C55E`;
- Warning `#F59E0B`;
- Ember `#EF4444`;
- semantic mana blue and movement green.

Brass carries application structure, active navigation, and progression. Cyan is restricted to interaction/technical state. Resource colors remain semantic.

Typography:

- interface: high-readability sans-serif;
- transcript, commands, script editor, logs: monospace;
- decorative serif/display typography is optional and sparse.

## 12. Transcript invariants

The World transcript remains sacred:

- ANSI fidelity preserved;
- configurable text size preserved;
- prompt/user-command distinction preserved;
- highlight rules preserved;
- bounded rendered inline count preserved;
- full session transcript retained for search/logging;
- contextual completion retained;
- live-tail split retained;
- transcript and command input are never removed during workspace navigation.

## 13. Command bar invariants

- attached directly beneath World;
- empty string is valid MUD input;
- history navigation retained;
- Tab / Shift-Tab completion retained;
- password masking/history exclusion retained;
- pager/editor modes retained;
- provenance-aware central command dispatch retained.

## 14. Settings workspace architecture

The prior `SettingsWindow` top-level window is replaced by `SettingsWorkspace : UserControl`.

`SettingsWorkspace` owns form/editor state and persistence calls. It receives two callbacks:

- cancel/close request;
- saved/close request.

It does not own an OS window. `MainWindow` owns the overlay and decides how to reveal/hide it.

## 15. Compatibility and persistence

Existing `WorkspacePreferences.DockWidth` remains as the persisted default gameplay-rail width for compatibility. Tool ratios are product-defined rather than persisted in this first integrated-workspace slice. Existing OS-window placement state, if any, is no longer authoritative for primary tool navigation.

## 16. Success criteria

The 0.20.x architecture is successful when:

- NexMUD normally operates as one coherent application window;
- World is visually dominant and never disappears;
- Character state and XP progression are readable at a glance in World mode;
- room/context intelligence is immediately useful;
- Codex, Mapper, Automation, Scripting, Abilities, Jev, search, and Settings coexist with World while Character detail remains consolidated into the World rail;
- normal navigation spawns no OS tool windows;
- Settings is in-app;
- Mapper receives approximately half the body when selected;
- tool workspace switching preserves transcript state and command input;
- information density resembles a modern game client rather than an engineering dashboard;
- existing alpha functionality remains accessible after consolidation.

## 13. Default World visual contract (0.18.3)

This release treats the approved NexMUD concept image as the target for the default World view rather than as loose inspiration. The implementation remains data-driven by the existing gameplay view models, but its visual composition follows these rules:

- World uses layered obsidian/midnight surfaces with a deep console inset, not flat black rectangles.
- Structural framing uses muted antique-brass edges and dark inner frames. Bright gold is reserved for active/important emphasis.
- Primary text is parchment; metadata is neutral gray; cyan is restricted to interaction/technical focus; HP/MA/MV retain semantic red/blue/green.
- Main interface typography is materially larger than the alpha harness. The transcript remains configurable and defaults to 14 pt; gameplay labels/readouts target roughly 10.5–13.5 pt; primary HUD names/titles target roughly 17–21 pt.
- Historical note: the explicit in-content World header from 0.18.3 was removed in 0.20.0 as redundant with top navigation; the World frame remains visually explicit.
- Character gameplay composition is two-tier: foveal vitals/status beneath the transcript plus a compact expandable identity/detail pane in the World rail.
- Room / Context composition is scan-oriented: current room identity, immediate exits, populated named entity groups, recent observations, compact room memory, and pinned quick actions.
- Command entry is part of the World frame and receives a larger monospace input, cyan focus accent, and visually dominant brass Send action.
- The default rail is approximately 390–425 px with 370–445 px resize bounds. World receives all remaining width.
- Secondary workspace internals are explicitly out of scope for the 0.18.3 visual pass.
