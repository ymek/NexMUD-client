# NexMUD Gameplay Semantics, Event Normalization, and State Reconciliation Architecture

**Status:** Proposed implementation architecture
**Date:** 2026-09-28
**Product:** NexMUD
**Scope:** Cross-cutting correction and expansion of NexMUD's gameplay observation, semantic parsing, state reconciliation, command lifecycle, Mapper inputs, Automation events, Codex inputs, and replay/testing architecture based on expert Avendar gameplay logs.
**Jev constraint:** Jev must remain behaviorally unchanged in this slice. The architecture must make future Jev consumption cleaner, but Jev is explicitly not migrated, redesigned, or extended here.

## 1. Evidence Basis

This ARD is derived from four expert-player Avendar logs:

- `Mines 3_31_15.txt`
- `Xiganath.txt`
- `Void Drake Fight.txt`
- `Xiganath 06.27.2015.txt`

The corpus demonstrates behavior NexMUD must treat as normal rather than exceptional:

- commands are entered rapidly and may queue while the game is lagged;
- prompts, command echoes, combat text, group chatter, room output, and asynchronous game events interleave;
- `clear` is an Avendar game command that clears the server-side queued command buffer;
- resource values may temporarily exceed displayed maxima;
- group HP may be negative;
- active effects have multi-line modifiers, fractional durations, zero durations, and permanent durations;
- multiple identical mobs, corpses, and objects may occupy the same room;
- a character may transform into another visible form;
- room transitions may occur through cardinal movement, follow, flee, crawl, portals, summons, spoken teleport phrases, and other special mechanics;
- multiple distinct rooms may render only `It is pitch black ...`;
- blocked movement does not imply the current room is known;
- scans expose directional distance tiers without movement;
- output may expose coarse target health bands repeatedly during combat;
- item identification is structured, multi-line domain data;
- high-skill combat creates sustained high-volume output in which semantic signals are sparse relative to combat noise.

These observations require updates to several current NexMUD boundaries.

---

# 2. Decision

NexMUD will introduce a canonical gameplay observation and semantic-event layer between decoded game output and all domain consumers.

The target architecture is:

```text
Transport bytes
      |
      v
Telnet / MCCP / GMCP / ANSI decode
      |
      v
Game Frame Assembler
      |
      v
Immutable GameObservation stream
      |
      +-------------------------------+
      |                               |
      v                               v
Avendar Semantic Parser         Display Pipeline
      |                               |
      v                               v
Typed Semantic Events           WorldBuffer / UI
      |
      v
Session State Reducers
      |
      +----------+----------+----------+-----------+
      |          |          |          |           |
      v          v          v          v           v
 Character    Combat     Mapper      Codex     Automation
 State        State      State       Index      Events
```

The central rule is:

> Every subsystem consumes either immutable source observations or typed semantic events. No domain subsystem parses transformed UI text.

---

# 3. Why This Slice Exists

The existing architecture has progressively separated UI, Automation, Mapper, and scripting responsibilities, but expert gameplay demonstrates that the underlying game stream itself is more ambiguous and asynchronous than a simple line-oriented parser can safely model.

Several assumptions must be explicitly rejected:

```text
one command -> one response
one prompt -> one completed command
one movement command -> one named room
room name -> unique room identity
entity name -> unique entity
current resource <= max resource
one line -> one semantic fact
visible text -> canonical game state
```

All are contradicted by the corpus.

This slice establishes the normalized semantics every higher-level system should share.

---

# Part I: Source Observation Model

# 4. Immutable GameObservation

After protocol and ANSI decoding, NexMUD should create immutable source observations.

Conceptual model:

```csharp
public sealed record GameObservation(
    long Sequence,
    DateTimeOffset ReceivedAt,
    SessionId SessionId,
    ObservationKind Kind,
    string RawText,
    string PlainText,
    IReadOnlyList<AnsiRun> AnsiRuns,
    ObservationMetadata Metadata);
```

Initial kinds:

```text
Text
PromptCandidate
ProtocolEvent
LocalCommandEcho
ConnectionEvent
ReplayMarker
```

The observation is evidence.

It must not be mutated by:

- highlighting;
- substitution;
- gagging;
- semantic parsing;
- Mapper reconciliation;
- Automation;
- UI formatting.

---

# 5. Sequence Is Authoritative Ordering

Every inbound observation receives a monotonically increasing session sequence number.

Use sequence ordering for:

- semantic event ordering;
- state reduction;
- replay;
- correlation;
- diagnostics.

Do not use the MUD's displayed clock as transport/event ordering.

The game clock is domain data, not a reliable wall-clock timestamp.

---

# 6. Framing Must Tolerate Interleaving

The frame assembler must support cases where:

- prompt text and server text arrive in the same network chunk;
- a command echo appears after unrelated output;
- multiple commands were submitted before responses arrive;
- a game message appears directly after the prompt marker;
- a prompt is repeated with no meaningful output between prompts;
- ANSI sequences span transport reads.

No parser may assume TCP read boundaries correspond to game lines.

---

# 7. Source Preservation

For every meaningful observation preserve:

```text
RawText
PlainText
ANSI/style runs
sequence
receive time
session
prompt classification
local/server source
```

Later logging must be able to choose between raw-ish source and rendered player view.

---

# Part II: Command Lifecycle

# 8. CommandIntent

Every outbound command enters the existing central command dispatcher as a typed intent.

```text
CommandIntent
  CommandId
  SessionId
  SourceKind
  Text
  CreatedAt
  CorrelationId
  ParentOperationId
```

Source kinds remain distinct:

```text
User
Alias
Keybinding
Automation
Mapper
Script
FutureJev
```

---

# 9. Do Not Model One Command / One Response

Avendar permits command buffering.

The logs show commands submitted in bursts and responses arriving later among unrelated combat/output.

Therefore:

> A prompt is not an acknowledgement that exactly one prior client command completed.

NexMUD should journal outbound commands, but must not invent exact server completion correlation unless the protocol/game gives explicit evidence.

---

# 10. Outbound Command Journal

Maintain a bounded per-session command journal:

```text
OutboundCommandRecord
  CommandId
  SourceKind
  Text
  SentSequence
  SentAt
  State
```

Initial state should be intentionally conservative:

```text
Dispatched
TransportWritten
ServerQueueCleared
SessionEnded
```

Do not create false states such as `ExecutedSuccessfully` from a generic prompt.

---

# 11. Avendar `clear`

`clear` is a server command.

It must:

- be sent to the game;
- remain in user command history like any manual command;
- preserve User provenance;
- not clear NexMUD scrollback;
- not clear NexMUD logs;
- not clear NexMUD replay data;
- not clear completion indexes;
- not clear Automation history;
- not clear the outbound journal itself.

When the game emits:

```text
Buffer cleared.
```

the Avendar adapter may emit:

```text
game.commandQueueCleared
```

That event means the game reports its lagged command buffer was cleared.

It does not mean NexMUD can identify exactly which previously dispatched commands executed versus were discarded unless further evidence exists.

---

# 12. Client Clear Must Have a Different Identity

If NexMUD exposes a local clear-scrollback action, it must be named internally and in action identifiers something unambiguous such as:

```text
ClearWorldBuffer
```

Never map the word `clear` implicitly to a client action.

---

# 13. Never Coalesce User Commands

Repeated commands are meaningful during expert combat.

Do not deduplicate:

```text
str
str
str
```

Do not coalesce repeated movement or combat commands unless the user explicitly configures such behavior.

---

# Part III: Prompt Architecture

# 14. Prompt Is a Structured Observation

Prompt parsing belongs in the Avendar adapter.

A prompt may contain:

```text
current/max hp
current/max mana
current/max movement
tnl
exploration points
game time
terrain
light
other configured fields
```

Prompt configuration varies between characters/logs.

Do not require every optional field.

---

# 15. Prompt Grammar

Use a composable parser rather than one monolithic regex tied to one exact prompt.

Recommended shape:

```text
PromptParser
  ResourceSegmentParser
  TnlParser
  ExplorationParser
  GameClockParser
  TerrainParser
  LightParser
  ExtensionParser
```

Unknown prompt fields should be preserved as opaque tokens where feasible rather than causing total parse failure.

---

# 16. Resource Values Must Not Be Clamped

The corpus contains current HP and mana values exceeding the displayed maximum.

Therefore:

```text
current > maximum
```

is valid observational data.

Do not clamp in:

- parser;
- state reducer;
- HUD model;
- scripting API;
- replay fixtures.

Rendering may visually cap a percentage bar if desired, but the underlying numeric values must remain intact.

---

# 17. Signed Resource Values

Group output demonstrates negative HP.

Resource parsing must accept signed current values.

Conceptually:

```csharp
public sealed record ResourceValue(
    int Current,
    int Maximum);
```

Do not use unsigned numeric types.

---

# 18. Prompt Snapshot Event

Emit:

```text
character.promptUpdated
```

with a DTO such as:

```text
CharacterPromptSnapshot
  Health
  Mana
  Movement
  Tnl?
  ExplorationPoints?
  GameClock?
  Terrain?
  Light?
  UnknownFields
```

This is the authoritative fast-changing self-resource snapshot when available.

---

# Part IV: State Reconciliation

# 19. Observation vs Canonical State

NexMUD must distinguish:

```text
Observation
```

from:

```text
Current best-known state
```

Semantic parsers emit facts.

Reducers reconcile facts into current state.

No parser directly mutates the UI or Mapper graph.

---

# 20. State Evidence

State records that are derived from game output should retain:

```text
LastObservedSequence
SourceKind
Confidence / ResolutionQuality where useful
```

This is especially important for:

- current room;
- room occupants;
- combat target;
- group member resources;
- effects;
- target condition.

---

# 21. No False Precision

The logs expose target health bands such as:

```text
90%-100%
75%-90%
50%-75%
30%-50%
15%-30%
0%-15%
```

Represent these as ranges.

Do not convert them to invented exact percentages.

```text
ConditionRange
  MinPercent
  MaxPercent
  Descriptor
```

---

# Part V: Group State

# 22. Group Snapshot Parser

`gr` output should produce a typed group snapshot.

```text
GroupSnapshot
  LeaderName
  Members[]
```

Each member:

```text
GroupMemberSnapshot
  DisplayName
  Level
  ClassCode
  Health
  Mana
  Movement
  ObservedSequence
```

---

# 23. Group Identity Is Not Name Identity

A visible group entry may represent:

- a player;
- a charmed mob;
- a transformed player;
- another entity form.

The corpus includes a high-level character represented as `A large hen`.

Therefore, group identity must not assume:

```text
display name == permanent player identity
```

If NexMUD cannot resolve a stable identity, preserve the observation without forcing a merge.

---

# 24. Group Snapshots Are Observational

A group listing is a point-in-time observation.

It should update group state, but not retroactively rewrite unrelated event history.

Group data may lag combat events.

Sequence order remains authoritative.

---

# Part VI: Effects and Status

# 25. Active Effect Model

`af` output should parse into typed active effects.

```text
ActiveEffect
  Name
  Kind
  Duration
  Modifiers[]
  SourceSequence
```

Kinds observed include:

```text
Spell
Skill
Unknown
```

---

# 26. Effect Duration

Duration must support:

```text
fractional hours
zero hours
permanent
unknown
```

Do not normalize `0 hours` to immediate deletion during parse.

The game may display an effect at zero shortly before expiry.

The state reducer may later remove it when explicit fade/removal evidence arrives.

---

# 27. Multi-Line Effect Modifiers

An effect may have multiple continuation lines:

```text
communion
  hp +...
  armor class ...
  saves ...
```

The parser must attach continuation modifiers to the preceding effect.

Do not treat each continuation line as a separate anonymous effect.

---

# 28. Effect Transition Events

Where explicit output supports them, emit semantic events such as:

```text
effect.applied
effect.removed
effect.refreshed
effect.statusSnapshot
```

Do not infer removal solely from absence in an unrelated partial output.

---

# Part VII: Entity Observation Model

# 29. EntityObservation

Room occupants, items, corpses, and visible players should normalize into room-scoped observations.

```text
EntityObservation
  ObservationId
  Kind
  DisplayText
  CanonicalCandidateName
  Qualifiers[]
  Count
  StateFlags[]
  RoomObservationId
```

Possible kinds:

```text
Player
Mob
Pet
Item
Corpse
Interactable
Unknown
```

Classification may remain `Unknown` when evidence is insufficient.

---

# 30. Duplicate Entities

The corpus contains counted duplicates such as multiple identical corpses and repeated identical mobs.

Do not key room entities solely by normalized display name.

Represent:

```text
count
```

when the game explicitly gives a count.

Where individually interacting with duplicates matters, create room-scoped occurrence identities.

---

# 31. Qualifiers Are Structured

Examples include:

```text
White Aura
Translucent
Hide
Charmed
Plagued
Glowing
```

Parse recognized qualifiers into structured flags while retaining raw qualifier text.

Unknown qualifiers must not make entity parsing fail.

---

# 32. Entity Transformation

A character may appear in another form.

Do not automatically create a permanent Codex identity equating a transformed form with a player unless explicit evidence establishes the relation.

Transformation evidence may be represented separately:

```text
entity.formChanged
```

when directly observed.

---

# Part VIII: Room and Mapper Semantics

# 33. RoomObservation

A named room description should produce:

```text
RoomObservation
  ObservationId
  Name
  Description
  Exits[]
  Entities[]
  SequenceRange
  VisibilityQuality
```

---

# 34. ExitObservation

Do not model exits as plain strings.

Use:

```text
ExitObservation
  Direction
  RawToken
  Qualifiers[]
```

The corpus contains decorated exit tokens such as:

```text
(east)
```

The meaning of parentheses is not established by this corpus alone.

Preserve the qualifier without inventing semantics.

---

# 35. Opaque / Pitch-Black Rooms

`It is pitch black ...` is not a room identity.

Multiple distinct locations produce the same text.

Therefore:

- never merge all pitch-black observations into one room;
- never use `It is pitch black ...` as a room name;
- do not create a durable room graph node solely from that string unless the Mapper's route context can safely create a provisional location;
- retain the movement observation even when room identity is unknown.

Recommended:

```text
VisibilityQuality = Opaque
RoomIdentity = Unknown / Provisional
```

---

# 36. Movement Is Broader Than Cardinal Commands

Movement can result from:

```text
north/east/south/west/up/down
follow
flee
crawl
enter portal
spoken teleport phrase
summon
forced movement
other game mechanics
```

Mapper reconciliation must consume semantic movement/room-transition events, not only commands that look like directions.

---

# 37. MovementCause

Introduce:

```text
MovementCause
  ManualDirection
  MapperRoute
  Follow
  Flee
  Crawl
  Portal
  Teleport
  Summon
  Forced
  Unknown
```

This is observational/provenance data.

Not every cause will be known.

---

# 38. Movement Result

Normalize movement outcomes:

```text
SucceededKnownRoom
SucceededUnknownRoom
Blocked
CombatRestricted
Forced
Teleported
Disconnected
Unknown
```

Example distinction:

```text
Alas, you cannot go that way.
```

is a blocked movement observation.

```text
It is pitch black ...
```

after a movement command is evidence that movement may have succeeded but room identity remains unresolved.

---

# 39. Following

`You follow <name>.` followed by room output is movement evidence.

Mapper state must update even though the user did not issue the direction personally.

Future route/autopilot logic must not assume user-generated direction commands are the only source of Mapper movement.

---

# 40. Scan Is Not Movement

`scan` output provides directional observations at distance tiers.

Model separately:

```text
ScanObservation
  Direction
  Tiers[]
```

Each tier:

```text
Distance
Entities[]
Visibility
```

Do not insert scan targets into the current room occupant collection.

---

# Part IX: Combat Semantics

# 41. Combat Event Layer

The parser should normalize important combat semantics without attempting to turn every flavor line into a bespoke event type.

Initial structured events:

```text
combat.started
combat.ended
combat.targetObserved
combat.damageObserved
combat.attackMissed
combat.rescue
combat.disarm
combat.statusApplied
combat.statusRemoved
combat.fleeSucceeded
combat.fleeFailed
combat.movementRestricted
entity.died
```

Keep generic fallback:

```text
combat.observation
```

for unclassified combat text.

---

# 42. Combat Target Condition

Repeated target-condition lines are valuable state.

Emit:

```text
combat.targetConditionUpdated
```

with coarse condition range.

Do not let repeated identical condition lines flood expensive downstream processing unnecessarily; state reducers may coalesce unchanged derived state while the immutable source/event journal remains complete.

---

# 43. Combat Output Volume

High-volume combat is normal.

Semantic parsing must not require synchronous UI work.

Processing path:

```text
observation
  -> adapter parse
  -> semantic event queue
  -> reducers
  -> UI observations
```

No parser may synchronously repaint the transcript or HUD.

---

# 44. Combat Restrictions

Messages such as inability to crawl/move while fighting must become structured failure reasons where reliable.

This should feed:

- Mapper movement results;
- Automation;
- diagnostics;
- future Jev.

Do not bury them exclusively in display text.

---

# Part X: Items, Equipment, and Codex

# 45. Structured Item Identification

The corpus contains structured `id` output with fields including:

```text
Object
Flags
Weight
Size
Level
Material
Type
Spell
```

Parse into:

```text
ItemIdentification
  DisplayName
  Flags[]
  Weight?
  Size?
  Level?
  Material?
  Type?
  Properties[]
  Spells[]
  RawFields
```

---

# 46. Unknown Fields Must Survive

Future item types may expose fields not currently known.

Store unknown key/value rows in:

```text
RawFields
```

Do not discard them or make parsing fail.

---

# 47. Equipment Transitions

Recognize self equipment transitions such as:

```text
wear
remove
wield
dual wield
hold
stop using
```

and observed transitions for other entities where useful.

These should feed typed equipment state rather than only Codex text.

---

# 48. Corpse and Loot Semantics

The corpus includes:

```text
corpse creation
multiple corpses
get from corpse
coin split
trophy extraction
sacrifice
corpse disappearance
```

Normalize at least:

```text
entity.died
corpse.observed
item.looted
currency.received
corpse.harvested
corpse.sacrificed
```

where evidence is reliable.

Do not make auto-loot depend on visual buffer text.

---

# Part XI: Player and World Status

# 49. Hunger, Thirst, and Readiness

Messages such as:

```text
You are hungry.
You are thirsty.
You feel ready to ...
```

should be eligible for semantic status events.

Recommended generic shape:

```text
character.statusChanged
```

with typed known statuses where stable.

---

# 50. Game Clock

Parse the in-game clock separately:

```text
GameClock
  Hour
  Minute
```

Calendar/date transitions may later extend this model.

Never use game clock as networking or event timestamp.

---

# 51. Exploration and Progress

Where reliably parsed, exploration/experience gains should emit typed progression events.

This is useful for:

- session summaries;
- future logging;
- Codex/discovery;
- replay analysis.

It is not required to drive Core correctness.

---

# Part XII: Automation and Scripting

# 52. Semantic Events First

Automation should prefer semantic events when NexMUD understands the domain.

Examples:

```text
character.promptUpdated
character.effectApplied
group.snapshotUpdated
combat.targetConditionUpdated
movement.blocked
room.observed
scan.updated
item.identified
game.commandQueueCleared
```

Raw text triggers remain available for game behavior NexMUD does not model.

---

# 53. Preserve Raw Text Trigger Compatibility

Do not remove text triggers.

The event architecture must support both:

```text
semantic trigger
raw output trigger
```

without one contaminating the other.

---

# 54. Script DTOs

Expose immutable projections.

Do not expose:

- parser objects;
- mutable Core state;
- Mapper repositories;
- raw CLR entities.

---

# Part XIII: Output Transformation

# 55. Transformation Remains Presentation-Only

The current Input/Keybinding/Output Transformation work remains valid but must consume the new immutable observation stream.

Order:

```text
GameObservation
     |
     +--> semantic parser
     |
     +--> display transform
```

Never:

```text
display transform -> semantic parser
```

---

# 56. Gagging Combat Noise

The corpus strongly justifies gag/highlight/substitution features because expert combat output is dense.

A user may gag repeated combat flavor while NexMUD still emits and consumes semantic events derived from the original observation.

---

# 57. Local Echo vs Server Echo

Do not assume a command-looking line is necessarily a new user submission.

Preserve explicit local command submission records independently from server text/echo.

This is necessary when queued commands and server output interleave.

---

# Part XIV: Mapper Updates

# 58. Mapper Input Contract

Mapper should consume:

```text
room.observed
movement.succeeded
movement.blocked
movement.unknownDestination
movement.forced
movement.teleported
scan.updated
```

It should not parse world-buffer text.

---

# 59. Provisional Topology

For opaque movement:

```text
known room A
  -> direction east
  -> successful movement evidence
  -> unknown room
```

the Mapper may maintain provisional route-local topology if existing architecture supports it safely.

It must not merge unknown destinations simply because their visible output is identical.

---

# 60. Route Reconciliation

Any movement event not initiated by active Mapper orchestration may invalidate the active route assumption.

Examples:

```text
follow
flee
summon
teleport
forced move
manual movement
```

The route executor must reconcile from the next authoritative room observation.

---

# Part XV: Codex Updates

# 61. Codex Consumes Structured Observations

Codex classification should consume typed observations:

```text
EntityObservation
ItemIdentification
RoomObservation
CorpseObservation
```

not regex the rendered transcript independently.

This directly addresses prior misclassification problems.

---

# 62. Conservative Classification

When evidence is ambiguous:

```text
Unknown
```

is preferable to confidently misclassifying scenery/interactables as mobs.

Raw evidence should remain attached for later refinement.

---

# Part XVI: Logging and Replay

# 63. Corpus-Compatible Replay

The replay system must be able to ingest recorded source text and reproduce:

```text
observations
semantic events
state transitions
Mapper reconciliation
Automation trigger decisions
display transformations
```

without a live connection.

---

# 64. Replay Is a First-Class Parser Test

Every parser change should be testable against extracted fixtures from these logs.

The four logs become an initial regression corpus.

Do not use them only as manual reference.

---

# 65. Raw and Semantic Logs

Future logging should be able to persist:

```text
source observations
semantic events
rendered output
```

as distinct layers.

The user may only see one log format, but architecture must retain separation.

---

# Part XVII: Corpus Fixture Architecture

# 66. Fixture Extraction

Create stable test fixtures from representative ranges, not the entire files in every unit test.

Recommended fixture categories:

```text
prompt-basic
prompt-extended-terrain-light
prompt-current-over-max
group-normal
group-negative-hp
effects-multiline
item-id-structured
room-standard
room-duplicate-corpses
room-opaque-darkness
scan-distance-tiers
movement-blocked
movement-follow
movement-crawl
movement-portal
movement-teleport
movement-flee
combat-high-volume
target-condition-ranges
command-burst
command-clear-buffer
corpse-loot
```

---

# 67. Golden Semantic Event Tests

Each fixture should have expected semantic output.

Example:

```text
fixture: movement-blocked.txt

expected:
  MovementBlocked {
    reason: NoExit
  }
```

Example:

```text
fixture: prompt-current-over-max.txt

expected:
  health.current = 1054
  health.maximum = 1044
  mana.current = 600
  mana.maximum = 580
```

No normalization should alter the observed values.

---

# 68. Golden State Tests

In addition to parser events, replay a fixture sequence through reducers and assert canonical state.

This catches bugs where:

```text
parser is correct
but reducer corrupts meaning
```

---

# 69. Performance Corpus

Use high-volume combat sections as throughput fixtures.

Track:

```text
observations/sec
semantic parse latency
event queue depth
state reduction latency
UI batch latency
allocation pressure
```

Do not optimize by dropping authoritative source observations.

---

# Part XVIII: Diagnostics

# 70. Evidence Traceability

For any derived state, diagnostic tooling should be able to answer:

```text
What observation caused this?
What parser produced the event?
What sequence updated this state?
```

Recommended correlation:

```text
Observation.Sequence
SemanticEvent.EventId
SemanticEvent.SourceSequence
Reducer update
```

---

# 71. Parser Diagnostics

Unknown/unparsed lines should not normally be warnings.

High-volume MUD output contains enormous flavor variety.

Use:

```text
Trace/Debug: unclassified
Warn: malformed known structure
Error: parser invariant failure
```

Avoid log flooding.

---

# Part XIX: Impacted Current Systems

# 72. Required System Updates

This ARD intentionally affects multiple existing systems.

### Transport / Protocol Layer

Update to provide stable decoded observation input and sequencing.

### Avendar Adapter

Becomes the owner of semantic parsing and structured game-specific interpretation.

### Core State

Moves toward reducers consuming typed events rather than direct text-derived mutations.

### Input / Command Dispatcher

Adds outbound command journal semantics and explicit Avendar `clear` handling.

### World / Output Pipeline

Consumes immutable observations without becoming a semantic source.

### Automation

Prefers semantic events but retains raw text triggers.

### Mapper

Consumes room/movement/scan semantics and handles unknown/forced transitions.

### Codex

Consumes typed entity/item/room observations.

### HUD

Uses structured prompt/group/effect state; does not clamp resources.

### Replay

Becomes the primary regression harness for real-game semantics.

### Logging

Must preserve source/rendered/semantic separation for later productization.

### Jev

No behavior change. Future Jev will consume these stable semantic states/events.

---

# Part XX: Migration Strategy

# 73. Phase 1 - Observation Envelope

Implement:

```text
GameObservation
session sequence
source preservation
frame assembly
replay injection
```

Do not change current user-visible behavior yet.

---

# 74. Phase 2 - Prompt and Command Semantics

Implement:

```text
composable prompt parser
signed/unclamped resources
game clock separation
outbound command journal
game.commandQueueCleared
```

Update HUD/state consumers.

---

# 75. Phase 3 - Room and Movement Semantics

Implement:

```text
RoomObservation
ExitObservation
opaque room handling
MovementCause
MovementResult
follow/flee/crawl/portal/teleport/summon handling
ScanObservation
```

Update Mapper to consume only typed semantics.

---

# 76. Phase 4 - Group, Effects, Combat

Implement:

```text
GroupSnapshot
ActiveEffect
condition ranges
combat events
movement restrictions
entity death
```

Update HUD and Automation event surfaces.

---

# 77. Phase 5 - Entities, Items, Corpse/Codex

Implement:

```text
EntityObservation
duplicate/count handling
ItemIdentification
equipment transitions
corpse/loot/trophy semantics
```

Update Codex and item hover/detail models.

---

# 78. Phase 6 - Legacy Parser Removal

Audit for domain parsing outside the Avendar adapter.

Remove or redirect:

```text
MainWindow parsing
Mapper regex parsing
Codex regex parsing
Automation-specific semantic parsing
HUD-specific parsing
```

Text-trigger matching remains intentionally separate.

---

# Part XXI: Architectural Constraints

# 79. Forbidden Shortcuts

Do not:

- parse transformed display text for domain state;
- assume prompt equals command acknowledgement;
- clamp resource values;
- use unsigned HP/mana/movement values;
- key entities by display name alone;
- key rooms by room name alone;
- merge all pitch-black observations;
- assume movement only comes from directions;
- assume one line contains one semantic event;
- hard-code one exact prompt layout;
- infer exact health from condition ranges;
- erase unknown exit qualifiers;
- let UI controls become canonical state stores;
- introduce Jev-specific parser paths.

---

# 80. Compatibility

Existing gameplay should remain usable throughout migration.

Use strangler seams:

```text
new semantic path
   + legacy consumer adapter where necessary
```

rather than an all-at-once rewrite.

Temporary compatibility adapters must be clearly marked and removed after the corresponding subsystem migrates.

---

# Part XXII: Acceptance Criteria

# 81. Observation Layer

Complete when:

- every decoded inbound unit has stable session ordering;
- raw/plain/ANSI forms remain available;
- UI transformations cannot alter semantic source;
- replay can inject equivalent observations.

---

# 82. Prompt and Command Layer

Complete when:

- multiple prompt shapes parse;
- optional terrain/light fields are supported;
- current resource may exceed max;
- negative resource values parse where emitted;
- game time is separated from wall time;
- command bursts are preserved;
- prompts do not falsely acknowledge commands;
- `clear` remains a game command;
- `Buffer cleared.` emits server queue-clear semantics without clearing client state.

---

# 83. State Layer

Complete when:

- group snapshots are structured;
- transformed/display names do not force false identity merges;
- effect snapshots support continuation modifiers and fractional/zero/permanent durations;
- target health remains a range;
- derived state retains source sequence.

---

# 84. Mapper Layer

Complete when:

- named rooms reconcile normally;
- opaque darkness does not create false room identity;
- blocked movement is distinct from unknown successful movement;
- follow movement updates current room;
- crawl/portal/flee/teleport/summon transitions can update Mapper state;
- scan data does not pollute current-room occupants;
- active routes reconcile after external movement.

---

# 85. Entity / Codex Layer

Complete when:

- duplicate entities and corpse counts are preserved;
- recognized qualifiers become structured flags;
- unknown qualifiers remain preserved;
- structured item ID data populates item models;
- unknown item fields survive;
- Codex consumes semantic observations rather than rendered text.

---

# 86. Combat Layer

Complete when:

- important combat state changes are semantic;
- coarse health conditions remain ranges;
- movement restrictions become structured failure reasons;
- high-volume combat parsing does not block the UI;
- replay reproduces combat state transitions.

---

# 87. Regression Corpus

Complete when representative fixtures from all four source logs exist and run in CI.

The suite must cover at minimum:

```text
prompt variants
over-max resources
negative HP
command burst
clear
darkness maze
blocked movement
follow
special movement
group
effects
scan
item ID
duplicate corpses
combat condition
high-volume combat
```

---

# Part XXIII: Stop Point

After this semantic/state normalization slice, return to standard-client parity work.

Do not move Jev forward yet.

Recommended subsequent work remains:

```text
Profiles / Sessions / Connection Management
Logging / Replay Productization
User Scripting UX
Packages / Import-Export
Custom UI / Extensibility
Jev Migration LAST
```

The normalized semantic layer should make all of those safer.

---

# 88. Final Architectural Rule

Expert Avendar play is asynchronous, lossy, ambiguous, and high-volume.

NexMUD must not pretend the game is a request/response protocol.

The client should preserve evidence first, derive semantics second, reconcile state third, and let UI, Automation, Mapper, Codex, and eventually Jev consume the same typed truth.

```text
Preserve -> Parse -> Reconcile -> Consume
```

That becomes the canonical gameplay data flow for NexMUD.
