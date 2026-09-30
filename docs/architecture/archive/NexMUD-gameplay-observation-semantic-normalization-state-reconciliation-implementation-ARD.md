# NexMUD Gameplay Observation, Semantic Normalization, and State Reconciliation Architecture

**Status:** Implementation ARD
**Product:** NexMUD
**Scope:** Canonical inbound gameplay data flow, Avendar semantic parsing, command lifecycle, normalized domain events, state reconciliation, Mapper/Codex/Automation migration, and corpus regression coverage
**Target release:** Bump the current NexMUD MINOR version once when this slice is complete and accepted; reset PATCH to `0`. Do not choose an unrelated version number.
**Primary platform:** macOS, while preserving Windows/Linux compatibility
**Runtime:** .NET 10 / Avalonia
**Jev constraint:** Jev remains behaviorally unchanged. Do not migrate, redesign, extend, or opportunistically refactor Jev in this slice.
**Profiles constraint:** Existing Connection Profiles remain in place. Do not redesign Profiles/Sessions in this slice. The next architecture slice will do that work.

---

# 1. Agent Directive

This document is an implementation specification, not a design prompt.

Implement the architecture below as written.

Do not substitute a different event model, parser ownership model, state model, or migration order.

Do not reduce this work to "add more regexes."

Do not stop after introducing DTOs while existing Mapper, Codex, HUD, or Automation consumers continue independently parsing transcript text.

Do not introduce user-facing behavior unrelated to the requirements below.

Do not redesign Jev.

Do not redesign Connection Profiles.

Do not create a second semantic parser in the GUI.

Do not parse transformed/gagged/substituted text for Core state.

Do not infer domain facts where the source logs only provide ambiguous evidence.

Before packaging, produce an acceptance audit with every requirement marked:

```text
PASS
BLOCKED
NOT APPLICABLE
```

`DEFERRED` is not acceptable for requirements in this ARD.

---

# 2. Evidence Corpus

The implementation must use the four ingested expert-player logs as the initial behavioral corpus:

```text
Mines 3_31_15.txt
Xiganath.txt
Void Drake Fight.txt
Xiganath 06.27.2015.txt
```

These logs are not documentation samples. They are regression evidence.

The implementation must extract stable fixtures from them and commit those fixtures into the repository test suite.

The corpus demonstrates all of the following and the implementation must account for each:

```text
rapid queued commands
server-side command queue clearing
prompt/output/command interleaving
resource values above displayed maxima
negative group HP
fractional and zero-duration affects
multi-line affect modifiers
coarse target-health bands
duplicate corpses/entities
entity qualifiers
dark/opaque movement with no usable room name
blocked movement
follow movement
flee movement
crawl movement
portal movement
teleport/summon/forced movement
directional scan at distance
structured item identification
high-volume combat output
corpse/loot/equipment transitions
```

---

# 3. Core Architectural Decision

All decoded gameplay input must flow through one canonical observation and semantic normalization pipeline.

The target architecture is exactly:

```text
Transport bytes
      |
      v
Telnet / MCCP / GMCP / ANSI decoding
      |
      v
GameFrameAssembler
      |
      v
Immutable GameObservation
      |
      +----------------------------------+
      |                                  |
      v                                  v
AvendarSemanticParser              Display Pipeline
      |                                  |
      v                                  v
Typed Semantic Events             Output transforms
      |                                  |
      v                                  v
State Reducers                     WorldBuffer / UI
      |
      +----------+----------+----------+-----------+
      |          |          |          |           |
      v          v          v          v           v
Character     Combat     Mapper      Codex     Automation
State         State      State       Index      Events
```

The canonical rule is:

```text
Preserve -> Parse -> Reconcile -> Consume
```

No domain subsystem may skip this boundary.

---

# 4. Required Ownership

Use these ownership rules:

```text
Transport
  owns bytes and protocol decoding

GameFrameAssembler
  owns creation of immutable ordered observations

Avendar adapter
  owns Avendar text semantics and typed semantic-event production

Core reducers
  own current best-known runtime state

Mapper
  consumes room/movement/scan semantic events

Codex
  consumes typed room/entity/item observations

Automation
  consumes semantic events or raw-text trigger observations

Output transformation
  owns presentation only

WorldBuffer
  owns rendered transcript state

Jev
  unchanged
```

`MainWindow` must not become a parser, reducer, or state authority.

---

# Part I: Observation Envelope and Framing

# 5. GameObservation

Introduce one immutable canonical observation type.

Use this shape unless an existing equivalent already provides every field:

```csharp
public sealed record GameObservation(
    long Sequence,
    SessionId SessionId,
    DateTimeOffset ReceivedAt,
    ObservationKind Kind,
    string RawText,
    string PlainText,
    IReadOnlyList<AnsiRun> AnsiRuns,
    ObservationMetadata Metadata);
```

If `SessionId` does not exist yet, add a lightweight GUID-backed `SessionId` representing the current live connection lifetime.

Do not implement multi-session UI or SessionManager in this slice.

The later Profiles/Sessions ARD will own that expansion.

---

# 6. ObservationKind

Use exactly these initial kinds:

```text
Text
PromptCandidate
ProtocolEvent
LocalCommandEcho
ConnectionEvent
ReplayMarker
```

Do not proliferate Avendar-specific values in `ObservationKind`.

Avendar meaning belongs in semantic events.

---

# 7. Sequence

`Sequence` is:

```text
monotonically increasing
per SessionId
assigned before semantic parsing
never reused
```

It is the authoritative ordering mechanism for:

```text
semantic events
state reduction
replay
diagnostics
correlation
```

Do not use:

```text
game clock
render order
UI timestamp
TCP packet index
```

as semantic ordering.

---

# 8. Frame Assembly

`GameFrameAssembler` must operate after Telnet/MCCP/CHARSET/ANSI decoding.

It must tolerate:

```text
multiple logical lines in one transport read
one logical line across multiple reads
ANSI sequence boundaries across reads
prompt text with no newline
prompt followed immediately by game output
game output followed immediately by prompt
several user commands before corresponding output
asynchronous combat/group/environment output between command responses
```

Do not assume TCP reads correspond to MUD lines.

---

# 9. Source Preservation

For every observation preserve:

```text
RawText
PlainText
AnsiRuns
Sequence
ReceivedAt
SessionId
source metadata
```

Once created, `GameObservation` is immutable.

No transformation or semantic consumer may alter it.

---

# 10. Display Branch Independence

The display branch receives the same immutable observation.

It may:

```text
highlight
gag
substitute
timestamp
render local echo
```

It may not write changes back into:

```text
GameObservation
semantic parser input
Core state
Mapper state
Automation semantic events
Codex input
```

A gagged line remains available to semantics.

A substituted line does not become semantic source.

---

# 11. Replay Injection Boundary

Replay must inject at the `GameObservation` boundary.

Add an abstraction equivalent to:

```csharp
public interface IGameObservationSource
{
    IAsyncEnumerable<GameObservation> ReadAsync(
        CancellationToken cancellationToken);
}
```

Live transport and replay are separate producers of the same observation contract.

Do not build a second replay-only semantic path.

---

# 12. Phase-1 User Experience

Observation-envelope work must cause no intentional user-visible behavior change.

Existing rendering and parsing should continue through compatibility adapters until subsequent phases migrate them.

---

# Part II: Prompt and Command Lifecycle

# 13. Avendar Prompt Parser

The Avendar adapter owns prompt parsing.

Implement a composable parser.

Do not implement one monolithic regex matching one exact prompt string.

Required parser components:

```text
ResourceSegmentParser
TnlParser
ExplorationPointsParser
GameClockParser
TerrainParser
LightParser
UnknownPromptFieldCollector
```

---

# 14. Prompt Variants

Support both observed forms:

```text
<hp(max)hp mana(max)m movement(max)mv tnl ep>
```

and extended prompt forms containing optional:

```text
terrain
light
additional configured fields
```

Unknown optional tokens must not invalidate the entire prompt.

Preserve them in:

```text
UnknownFields
```

---

# 15. Resource Type

Use signed integers.

Conceptual model:

```csharp
public sealed record ResourceValue(
    int Current,
    int Maximum);
```

Do not use unsigned types.

Do not clamp.

---

# 16. Over-Max Resources

This is valid source data:

```text
Current > Maximum
```

Examples exist in the corpus.

The parser and state reducer must retain the exact observed numbers.

HUD rendering may cap a visual bar at 100% while continuing to display the real numeric values.

---

# 17. Negative Values

Negative current HP is valid observational data in group snapshots.

All shared resource DTOs must support it.

Do not add special-case unsigned group models.

---

# 18. Game Clock

Parse Avendar's in-game clock into:

```text
GameClock
  Hour
  Minute
```

Game clock is domain data.

Never use it for:

```text
observation ordering
real-time timeout logic
logging wall-clock timestamp
timer scheduling
```

---

# 19. Prompt Event

Emit:

```text
character.promptUpdated
```

Payload:

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
  SourceSequence
```

---

# 20. Outbound Command Journal

Add a bounded per-session outbound journal.

Use:

```text
OutboundCommandRecord
  CommandId
  SessionId
  SourceKind
  Text
  DispatchedAt
  SourceSequence?
  State
```

Initial state values are exactly:

```text
Dispatched
TransportWritten
ServerQueueCleared
SessionEnded
```

Do not invent:

```text
Executed
Succeeded
Acknowledged
Completed
```

from prompt arrival.

---

# 21. Prompt Is Not Command Acknowledgement

Remove any logic that assumes:

```text
prompt received
  == previous command completed
```

The expert logs demonstrate queued commands and unrelated interleaving output.

A prompt is a prompt observation and state snapshot only.

---

# 22. Avendar `clear`

`clear` is an Avendar game command.

Required behavior:

```text
user types clear
    |
normal input/alias path
    |
central command dispatcher
    |
Transport
```

It must not trigger a local NexMUD clear action.

It must be typed in full.

Do not implement client-side abbreviation such as:

```text
cl
cle
clea
```

for the game command.

If an alias explicitly maps one of those strings to `clear`, that alias remains user-configured Automation behavior and is allowed.

---

# 23. `Buffer cleared.`

When Avendar emits:

```text
Buffer cleared.
```

emit:

```text
game.commandQueueCleared
```

Then update unresolved journal records conservatively to:

```text
ServerQueueCleared
```

only where the current journal model can safely indicate they were pending after the clear request.

Do not claim exact server execution/discard status for individual commands unless explicit evidence exists.

Do not clear:

```text
WorldBuffer
client command history
logs
replay data
completion index
semantic event history
```

---

# 24. Client Clear Action

If NexMUD exposes a local transcript-clearing action, its internal identity must be:

```text
ClearWorldBuffer
```

or the existing equivalent explicit client action.

Never bind the bare command text `clear` to it.

---

# 25. Preserve Command Bursts

Do not:

```text
deduplicate
coalesce
rate-collapse
replace
```

repeated manually entered commands.

These are all distinct:

```text
str
str
str
```

unless the user explicitly configures Automation to behave otherwise.

---

# Part III: Semantic Event Contract

# 26. Base Semantic Event

All semantic events implement/equivalent:

```csharp
public interface IGameSemanticEvent
{
    Guid EventId { get; }
    SessionId SessionId { get; }
    long SourceSequence { get; }
    DateTimeOffset ObservedAt { get; }
}
```

Use existing project event abstractions if they already satisfy this contract.

Do not create a second event bus.

Publish through the existing canonical event pipeline.

---

# 27. Event Names

The scripting/Automation-visible stable names must use existing `domain.eventName` conventions.

Initial additions required by this ARD include:

```text
character.promptUpdated
character.statusChanged

group.snapshotUpdated

effect.snapshotUpdated
effect.applied
effect.removed

room.observed
movement.succeeded
movement.blocked
movement.unknownDestination
movement.forced
movement.teleported

scan.updated

combat.started
combat.ended
combat.targetObserved
combat.targetConditionUpdated
combat.damageObserved
combat.fleeSucceeded
combat.fleeFailed
combat.movementRestricted

entity.observed
entity.died

item.identified
equipment.changed
item.looted
currency.received
corpse.observed
corpse.harvested
corpse.sacrificed

game.commandQueueCleared
```

Do not expose C# class names as stable event names.

---

# Part IV: State Reconciliation

# 28. Reducer Rule

Semantic parsers emit observations/facts.

Reducers own canonical current state.

Required flow:

```text
GameObservation
    |
AvendarSemanticParser
    |
IGameSemanticEvent
    |
Reducer
    |
Current runtime state
```

Do not let parsers mutate HUD/view models/Mapper repositories directly.

---

# 29. Evidence Metadata

Derived state that may be ambiguous must retain:

```text
SourceSequence
ObservedAt
ResolutionQuality
```

Use exactly these `ResolutionQuality` values:

```text
Authoritative
Observed
Provisional
Unknown
```

Do not invent numeric confidence scores.

---

# 30. No False Precision

When the game reports a range, store a range.

Example model:

```text
ConditionRange
  Descriptor
  MinPercentInclusive
  MaxPercentInclusive
```

Do not translate:

```text
50%-75%
```

into:

```text
62%
```

---

# Part V: Room and Movement Normalization

# 31. RoomObservation

Introduce:

```text
RoomObservation
  ObservationId
  Name?
  Description?
  Exits[]
  Entities[]
  VisibilityQuality
  SourceSequenceStart
  SourceSequenceEnd
```

`Name` may be absent.

---

# 32. VisibilityQuality

Use exactly:

```text
Visible
Partial
Opaque
```

`It is pitch black ...` maps to:

```text
Opaque
```

It is not a room name.

---

# 33. Opaque Locations

Never create one durable room keyed by:

```text
It is pitch black ...
```

Multiple distinct locations emit that text.

For movement into darkness:

```text
movement evidence exists
room identity unresolved
```

represent:

```text
movement.unknownDestination
```

and allow Mapper to maintain provisional runtime topology if required.

Do not merge opaque destinations based on identical text.

---

# 34. ExitObservation

Use:

```text
ExitObservation
  Direction
  RawToken
  Qualifiers[]
```

Recognize:

```text
north
east
south
west
up
down
```

Preserve decorated raw tokens such as:

```text
(east)
```

Do not assign semantics to parentheses unless an Avendar rule is separately proven.

---

# 35. MovementCause

Use exactly:

```text
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

Do not reduce all movement to directional commands.

---

# 36. MovementResultKind

Use exactly:

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

---

# 37. Blocked Movement

Recognize reliable Avendar blocked movement output such as:

```text
Alas, you cannot go that way.
```

Emit:

```text
movement.blocked
```

with structured reason:

```text
NoExit
```

Do not mutate current room.

---

# 38. Combat Movement Restriction

Recognize reliable output such as:

```text
No way! You are still fighting!
```

Emit:

```text
combat.movementRestricted
movement.blocked
```

with reason:

```text
CombatRestriction
```

where a movement intent is currently active.

---

# 39. Follow

Recognize:

```text
You follow <name>.
```

as movement cause evidence.

The following room observation updates current room even though the user did not issue a direction.

Mapper must consume this.

---

# 40. Flee

Recognize successful flee output and resulting room observations.

Emit:

```text
combat.fleeSucceeded
movement.succeeded / movement.unknownDestination
```

as supported by evidence.

Failed flee remains:

```text
combat.fleeFailed
```

Do not invent destination direction if the output does not provide it.

---

# 41. Crawl

Recognize special movement such as:

```text
crawl south
```

and the resulting room transition.

Cause:

```text
Crawl
```

Do not require the command to equal a bare direction.

---

# 42. Portal / Teleport / Summon / Forced Movement

Normalize known transitions into the movement model.

Examples include:

```text
enter portal
spoken transport phrase
has summoned you
forced room change
```

Use:

```text
Portal
Teleport
Summon
Forced
```

only when source evidence supports the cause.

Otherwise use:

```text
Unknown
```

---

# 43. ScanObservation

Introduce:

```text
ScanObservation
  Direction
  Tiers[]
  SourceSequence
```

Each tier:

```text
Distance
Visibility
Entities[]
```

Scan observations do not mutate current room occupants.

They are directional remote observations.

---

# 44. Mapper Migration

Mapper must consume only typed:

```text
room.observed
movement.*
scan.updated
```

for these semantics.

Remove Mapper-owned regex parsing of transcript output once parity tests pass.

Mapper must not parse transformed `WorldBuffer` text.

---

# Part VI: Group, Effects, and Combat

# 45. GroupSnapshot

Parse `gr` output into:

```text
GroupSnapshot
  LeaderDisplayName?
  Members[]
  SourceSequence
```

Member:

```text
GroupMemberSnapshot
  DisplayName
  Level?
  ClassCode?
  Health
  Mana
  Movement
```

Resource values use signed ints.

---

# 46. Group Identity

Do not key permanent identity by `DisplayName`.

A transformed player may appear under another form.

Group snapshots are observational.

If stable identity cannot be proven:

```text
retain separate observed entity
do not force merge
```

---

# 47. EffectSnapshot

Parse `af` into:

```text
EffectSnapshot
  Effects[]
  SourceSequence
```

Each effect:

```text
ActiveEffect
  Name
  Kind
  Duration
  Modifiers[]
```

---

# 48. EffectKind

Use:

```text
Spell
Skill
Unknown
```

---

# 49. EffectDuration

Use a discriminated model supporting:

```text
Timed
Permanent
Unknown
```

For `Timed`, support decimal/fractional hours.

Do not round:

```text
2.5
0.5
```

to integers.

---

# 50. Zero Duration

An effect shown as:

```text
0 hours
```

remains in the snapshot.

Do not delete it during parsing.

Removal requires:

```text
subsequent authoritative snapshot absence
or
explicit removal/fade event
```

according to reducer semantics.

---

# 51. Multi-Line Modifiers

Continuation rows beginning with modifier data must attach to the immediately preceding effect.

Do not create anonymous effects.

---

# 52. Combat State

Maintain normalized combat state:

```text
InCombat
CurrentTarget?
TargetCondition?
LastCombatSequence
```

Do not attempt to fully model every combat flavor line.

---

# 53. Target Condition

Recognize coarse condition messages and preserve:

```text
Descriptor
MinPercentInclusive
MaxPercentInclusive
```

At minimum support observed bands:

```text
100%
90%-100%
75%-90%
50%-75%
30%-50%
15%-30%
0%-15%
```

Preserve descriptor text separately.

---

# 54. Combat Event Scope

Normalize important state changes only.

Required semantic categories:

```text
combat.started
combat.ended
combat.targetConditionUpdated
combat.damageObserved
combat.fleeSucceeded
combat.fleeFailed
combat.movementRestricted
entity.died
```

Do not create one C# event class for every adjective or attack sentence.

Unclassified combat text remains source observation data.

---

# 55. Combat Throughput

Semantic parsing and reduction must not synchronously repaint UI.

High-volume combat path:

```text
observation
  -> parse
  -> semantic event queue
  -> reducers
  -> batched UI projection
```

No synchronous transcript rebuild.

---

# Part VII: Entities, Items, and Codex

# 56. EntityObservation

Introduce:

```text
EntityObservation
  ObservationId
  Kind
  DisplayText
  CanonicalCandidateName?
  Qualifiers[]
  Count
  StateFlags[]
  SourceSequence
```

Kinds:

```text
Player
Mob
Pet
Item
Corpse
Interactable
Unknown
```

Default to `Unknown` when evidence is insufficient.

---

# 57. Duplicate Entities

Do not key room contents only by normalized name.

Support explicit counts such as:

```text
( 2) The corpse ...
```

Preserve:

```text
Count = 2
```

Where interaction requires distinguishing individual occurrences, use room-scoped occurrence IDs.

---

# 58. Qualifiers

Recognize at minimum observed qualifiers:

```text
White Aura
Translucent
Hide
Charmed
Glowing
```

Preserve unknown qualifier strings.

Unknown qualifiers must not cause parse failure.

---

# 59. Structured Item Identification

Parse `id` blocks into:

```text
ItemIdentification
  DisplayName
  Flags[]
  Weight?
  Size?
  Level?
  Material?
  Type?
  Spells[]
  Properties[]
  RawFields
  SourceSequence
```

---

# 60. Unknown Item Fields

Unknown identification rows go into:

```text
RawFields
```

Do not discard them.

Do not make parsing fail because a new Avendar field appears.

---

# 61. Equipment Transitions

Normalize reliable self equipment transitions:

```text
wear
remove
wield
hold
stop using
```

Emit:

```text
equipment.changed
```

Use typed slots only where existing domain semantics know the slot.

Otherwise preserve item/action data without guessing.

---

# 62. Corpse and Loot Semantics

Recognize reliable corpus evidence for:

```text
corpse observed
item looted
currency received
corpse harvested
corpse sacrificed
entity death
```

Emit corresponding events.

Do not let auto-loot/sacrifice parse transformed UI text.

---

# 63. Codex Migration

Codex must consume typed:

```text
RoomObservation
EntityObservation
ItemIdentification
Corpse observations/events
```

Remove independent Codex transcript regex paths once parity tests pass.

If classification is ambiguous, store:

```text
Unknown
```

rather than a false MOB/item classification.

---

# Part VIII: Character Status and Progress

# 64. Character Status

Recognize stable statuses where useful:

```text
hungry
thirsty
resting
standing
flying
```

Emit:

```text
character.statusChanged
```

only for reliably parsed transitions.

Do not infer unseen statuses.

---

# 65. Exploration / Experience Events

Where reliably parsed, emit:

```text
progress.explorationGained
progress.experienceGained
```

These are informational.

Do not make Core correctness depend on them.

---

# Part IX: Automation and Scripting

# 66. Semantic Trigger Preference

Automation should consume typed semantic events when NexMUD understands the domain.

Examples:

```text
character.promptUpdated
effect.snapshotUpdated
group.snapshotUpdated
movement.blocked
room.observed
scan.updated
combat.targetConditionUpdated
item.identified
game.commandQueueCleared
```

---

# 67. Raw Text Triggers Remain

Do not remove raw-text triggers.

Required trigger sources:

```text
semantic event
raw GameObservation text
```

They are separate.

Output substitution/gagging must not change raw trigger source.

---

# 68. Script DTOs

Expose immutable DTO projections.

Do not expose:

```text
Avendar parser objects
reducers
mutable Core stores
Mapper repositories
CLR domain internals
```

---

# Part X: Corpus Regression Harness

# 69. Fixture Directory

Create a dedicated fixture tree, for example:

```text
tests/Fixtures/Avendar/Corpus/
```

Do not make tests depend on user-local downloads.

Commit extracted fixture text into the repository.

---

# 70. Required Fixtures

Create at minimum:

```text
prompt-basic.txt
prompt-extended-terrain-light.txt
prompt-current-over-max.txt
command-burst.txt
command-clear-buffer.txt

room-standard.txt
room-opaque-darkness.txt
movement-blocked.txt
movement-follow.txt
movement-flee.txt
movement-crawl.txt
movement-portal.txt
movement-teleport-or-summon.txt
scan-distance-tiers.txt

group-normal.txt
group-negative-hp.txt

effects-multiline.txt
effects-fractional-zero-duration.txt

target-condition-ranges.txt
combat-high-volume.txt
combat-movement-restricted.txt

entity-duplicate-corpses.txt
entity-qualifiers.txt

item-id-structured.txt
equipment-transition.txt
corpse-loot.txt
```

Use actual corpus excerpts.

Do not synthesize these fixtures unless an exact edge case is absent from all source logs.

---

# 71. Fixture Provenance

Each fixture must include a sibling metadata file or source comment containing:

```text
source log filename
source line/range or extraction marker
purpose
```

Do not include player commentary as game output when the original log clearly marks commentary separately.

---

# 72. Golden Semantic Tests

For each fixture, assert exact semantic-event output.

Example:

```text
prompt-current-over-max

Health.Current  = 1054
Health.Maximum  = 1044
Mana.Current    = 600
Mana.Maximum    = 580
```

No clamping.

---

# 73. Golden Reducer Tests

Replay semantic events into reducers and assert resulting runtime state.

This is required separately from parser tests.

Parser correctness does not prove reducer correctness.

---

# 74. Mapper Golden Tests

Use corpus fixtures to verify:

```text
blocked movement leaves room unchanged
opaque successful movement does not merge dark rooms
follow updates room
crawl updates room
portal/teleport/summon can invalidate route assumptions
scan does not modify current-room occupants
```

---

# 75. Codex Golden Tests

Verify:

```text
duplicate corpse counts preserved
unknown entities remain Unknown
item ID structured fields populate correctly
unknown ID fields survive
qualifiers survive classification
```

---

# 76. High-Volume Combat Performance Test

Use a sustained real combat excerpt.

Measure at minimum:

```text
total observations
semantic events
elapsed parse/reduce time
peak event queue depth if exposed
allocations if existing test tooling supports it
```

Acceptance requirement:

```text
no UI-thread blocking
no dropped GameObservation records
no semantic ordering inversion
```

Do not add arbitrary microbenchmark pass/fail numbers unless the repository already has performance baselines.

---

# Part XI: Legacy Parser Removal

# 77. Inventory of Existing Parsers

Before removal, produce a code inventory of all text-semantic parsing locations.

Search at minimum:

```text
MainWindow
GUI view models
Core
Mapper
Codex
Automation
Avendar adapters
HUD/status parsing
transcript/output pipeline
```

---

# 78. Migration Rule

For each duplicated parser:

```text
identify semantic responsibility
add equivalent Avendar semantic event if missing
migrate consumer
add parity test
remove legacy parser
```

Do not leave two authoritative semantic parsers "temporarily" at final acceptance.

---

# 79. Allowed Remaining Text Matching

After this slice, text matching is still valid for:

```text
Avendar adapter parsing
raw text Automation triggers
output transform rules
search
user-defined script logic consuming raw observations
```

It is not valid as an independent hidden domain-state parser inside Mapper/Codex/HUD/MainWindow.

---

# Part XII: Diagnostics

# 80. Traceability

Every semantic event must carry:

```text
EventId
SessionId
SourceSequence
ObservedAt
```

Every reducer-derived state update must retain at least:

```text
LastSourceSequence
```

Diagnostic tooling must be able to answer:

```text
Which observation caused this state?
Which semantic event was produced?
```

---

# 81. Unclassified Text

Unclassified text is normal.

Do not log every unclassified line as Warning/Error.

Use:

```text
Trace/Debug = unclassified game text
Warn        = malformed structure matching a known parser family
Error       = parser invariant/internal failure
```

---

# Part XIII: Failure Isolation

# 82. Parser Failure

One parser failure must not stop the observation stream.

On failure:

```text
retain GameObservation
emit diagnostic
continue later observations
```

---

# 83. Reducer Failure

A reducer failure:

```text
records diagnostic with source event
does not corrupt unrelated state stores
does not disconnect transport
does not stop World rendering
```

Use existing fault-boundary conventions.

---

# 84. Mapper/Codex Consumer Failure

Mapper or Codex failure must not block:

```text
observation creation
semantic parsing
WorldBuffer rendering
other semantic consumers
```

---

# Part XIV: Incremental Implementation Order

# 85. Commit 1 - Observation Envelope

Implement exactly:

```text
SessionId lightweight connection-lifetime identity
GameObservation
ObservationKind
GameFrameAssembler
per-session Sequence
raw/plain/ANSI preservation
live observation source
replay observation source
```

Add envelope/framing tests.

No intentional UI change.

---

# 86. Commit 2 - Prompt and Command Lifecycle

Implement:

```text
composable prompt parser
signed/unclamped ResourceValue
GameClock
character.promptUpdated
OutboundCommandJournal
prompt-not-ack semantics
clear command correctness
game.commandQueueCleared
```

Add corpus fixtures/tests.

---

# 87. Commit 3 - Room and Movement

Implement:

```text
RoomObservation
VisibilityQuality
ExitObservation
MovementCause
MovementResultKind
blocked movement
opaque destinations
follow
flee
crawl
portal
teleport
summon/forced movement
ScanObservation
```

Migrate Mapper.

Remove Mapper duplicate parser paths.

---

# 88. Commit 4 - Group, Effects, Combat

Implement:

```text
GroupSnapshot
negative HP
EffectSnapshot
fractional/zero/permanent durations
multi-line modifiers
combat state
target condition ranges
movement restrictions
combat event categories
```

Migrate HUD/Automation consumers.

---

# 89. Commit 5 - Entities, Items, Codex

Implement:

```text
EntityObservation
counts
qualifiers
ItemIdentification
equipment transitions
corpse/loot semantics
```

Migrate Codex.

Remove Codex duplicate parser paths.

---

# 90. Commit 6 - Corpus Harness and Legacy Cleanup

Complete:

```text
all required corpus fixtures
golden semantic tests
golden reducer tests
Mapper golden tests
Codex golden tests
high-volume combat test
semantic parser inventory
legacy parser removal
```

Do not package until this commit is complete.

---

# Part XV: Full Validation

# 91. .NET Validation

Use the actual repository .NET 10 toolchain.

Before packaging run:

```text
dotnet restore
dotnet build
dotnet test
```

Run all existing repository static/invariant checks.

Do not package based solely on structural verification.

---

# 92. Regression Requirements

Existing functionality must remain working:

```text
connection
login/password masking
World rendering
ANSI
Automation
Mapper route execution
Scripting/Jint
output transforms
history/completion/keybindings
existing basic Profiles
```

Jev must compile and preserve its current behavior.

---

# Part XVI: Acceptance Criteria

# 93. Observation Acceptance

PASS only when:

- all decoded gameplay input becomes immutable ordered `GameObservation`;
- per-session ordering is monotonic;
- raw/plain/ANSI data is preserved;
- replay injects through the same observation contract;
- output transforms cannot contaminate semantics.

---

# 94. Prompt / Command Acceptance

PASS only when:

- observed prompt variants parse;
- unknown optional prompt fields do not destroy the parse;
- resource values are signed;
- over-max current resources remain exact;
- game clock is separated from wall time;
- prompts are not command acknowledgements;
- command bursts remain distinct;
- `clear` is sent as a game command;
- `Buffer cleared.` emits `game.commandQueueCleared`;
- client transcript state is not cleared by the game command.

---

# 95. Room / Movement Acceptance

PASS only when:

- named rooms normalize;
- opaque darkness is not treated as room identity;
- blocked movement leaves current room intact;
- successful unknown movement is distinct from blocked movement;
- follow/flee/crawl/portal/teleport/summon/forced movement can update Mapper state;
- scan remains remote observation;
- Mapper no longer owns duplicate transcript semantic parsing.

---

# 96. Group / Effects / Combat Acceptance

PASS only when:

- group snapshots are structured;
- negative HP is preserved;
- effect continuation rows attach correctly;
- fractional durations survive;
- zero-duration effects survive snapshots;
- permanent/unknown durations are representable;
- target condition remains a range;
- combat movement restrictions are structured;
- high-volume combat does not block UI.

---

# 97. Entity / Item / Codex Acceptance

PASS only when:

- duplicate entity counts survive;
- qualifiers survive;
- ambiguous entities may remain `Unknown`;
- item ID data is structured;
- unknown item ID fields survive;
- equipment changes are semantic events;
- corpse/loot semantics are available;
- Codex consumes typed observations instead of its own rendered-text parser.

---

# 98. Corpus Acceptance

PASS only when:

- fixtures from all four ingested logs are committed;
- each required fixture category exists;
- golden semantic tests pass;
- golden reducer tests pass;
- Mapper/Codex corpus tests pass;
- high-volume combat fixture passes;
- legacy duplicate domain parsers are removed.

---

# 99. Jev Acceptance

PASS only when Jev behavior is unchanged.

No new Jev semantics, policy, or UI belong to this release.

---

# 100. Explicit Non-Goals

Do not implement here:

```text
profile redesign
multi-session UI
new connection profile UX
logging product UI
package manager
general scripting IDE
custom UI widgets
Jev migration
cloud sync
stored passwords
auto-login
```

---

# 101. Next Architecture Slice

Only after this ARD is implemented and accepted, proceed to:

```text
NexMUD Profiles, Sessions, and Connection Management Architecture
```

Use the already-created Profiles ARD as the basis, but update its baseline to include this semantic normalization work before giving it to the programming agent.

Do not start the Profiles implementation concurrently with this migration.

---

# 102. Final Rule

Expert Avendar gameplay is not a request/response protocol.

It is an asynchronous event stream with queued commands, ambiguous observations, incomplete visibility, high-volume combat, and multiple routes to the same state change.

NexMUD must therefore:

```text
preserve evidence first
parse Avendar meaning second
reconcile current state third
let features consume typed state/events fourth
```

The programming agent is not authorized to collapse these stages.
