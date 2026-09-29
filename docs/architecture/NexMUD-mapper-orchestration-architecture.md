# NexMUD Mapper Orchestration Architecture

**Status:** Proposed implementation architecture  
**Date:** 2026-09-28  
**Product:** NexMUD  
**Subsystem:** Mapper orchestration on the shared scripting platform  
**Depends on:** NexMUD Scripting Platform Architecture; NexMUD Jint Integration Architecture; NexMUD Scripting Vertical Slice Architecture; NexMUD Scripting SDK and Automation Migration Architecture  
**Baseline:** NexMUD v0.25.0 / build 25000

## 1. Decision

The next architectural slice moves Mapper route execution onto the shared NexMUD scripting runtime while preserving the Mapper's graph, pathfinding, room identity, reconciliation, and persistence as typed C# domain services.

```text
Mapper UI / Jev / Automation
        |
        v
Route Request
        |
        v
C# Mapper Domain
  - graph
  - rooms/exits
  - pathfinding
  - reconciliation
  - persistence
        |
        v
Route Plan
        |
        v
Mapper Orchestration Script
        |
        v
nex.mapper + nex.events + nex.commands + nex.timers
        |
        v
Movement Coordinator
        |
        v
Central Command Dispatcher
        |
        v
Transport
```

The Mapper owns knowledge and algorithms. The scripting runtime owns route orchestration and long-lived behavioral execution.

## 2. Primary Goal

Replace bespoke Mapper/autopilot workflow execution with the same structured runtime now used by NexMUD scripting and Automation.

The slice must support:

- route calculation;
- route execution;
- one-step movement;
- movement confirmation;
- expected-vs-actual room reconciliation;
- blocked movement;
- unexpected movement;
- replanning;
- pause;
- resume;
- abort;
- disconnect cancellation;
- deterministic task ownership;
- command provenance;
- non-blocking UI behavior;
- replayable route tests.

The user-visible Mapper must retain existing behavior such as selecting a destination and navigating to it.

## 3. Non-Goals

This slice does not:

- move graph algorithms into TypeScript;
- move room persistence into TypeScript;
- expose raw Mapper repositories;
- expose SQLite;
- make user scripts responsible for maintaining map topology;
- add general `nex.codex`;
- migrate Jev reasoning;
- implement a generalized quest planner;
- make Automation the owner of route execution;
- redesign the visual map except where required to surface route state;
- add arbitrary JavaScript hooks to every movement step.

## 4. Architectural Rule

> C# decides what the map is and how paths are calculated. Script orchestration decides how a route is executed over time.

The following remain in C#:

```text
room identity
room graph
exit graph
pathfinding
path cost
room merge/reconciliation
room discovery
exit discovery
persistence
route-plan generation
movement semantic extraction
```

The following move into shared orchestration:

```text
execute next step
await movement result
pause
resume
abort
retry
replan
recover
finish
emit route lifecycle
```

## 5. Why Route Execution Moves

A route executor is inherently a long-lived workflow:

```text
command
    |
wait for semantic result
    |
inspect state
    |
make next decision
    |
possibly delay/retry/replan
```

Building this directly into Mapper-specific background tasks duplicates behavior already provided by the scripting platform: lifecycle, cancellation, timers, event subscriptions, host operations, diagnostics, provenance, replay, and fault isolation.

The Mapper should not grow a second workflow engine.

# Part I: Domain Model

## 6. Mapper Domain Responsibilities

The typed Mapper domain remains authoritative for:

```text
MapGraph
Room
Exit
Direction
RoomIdentity
RoutePlan
RouteStep
Pathfinding
Reconciliation
MapPersistence
```

Conceptual contracts:

```csharp
public interface IMapperQueryService
{
    RoomSnapshot? GetCurrentRoom();
    RoutePlan? FindPath(RoomId destination);
}

public interface IMapperReconciliationService
{
    ReconciliationResult Reconcile(RoomObservation observation);
}
```

Names may differ from existing code. The architectural boundary matters more than exact type names.

## 7. Route Plan

The domain pathfinder returns a route plan, not executable behavior.

```text
RoutePlan
  RouteId
  StartRoomId
  DestinationRoomId
  GraphVersion
  Steps[]
```

Each step:

```text
RouteStep
  Sequence
  FromRoomId
  Direction
  ExpectedRoomId
  ExitMetadata
```

The route plan is immutable from the script's perspective. Replanning creates a new route plan.

## 8. Route Request

A route begins from a typed request:

```text
RouteRequest
  Destination
  Options
  Source
```

Initial destination should resolve to a room identity before orchestration begins.

For v1:

```text
RoomDestination
```

Existing "navigate to MOB" behavior may resolve the selected target to a known room in C# and then create a room route request. Do not require `nex.codex` merely to start Mapper migration.

# Part II: `nex.mapper`

## 9. SDK Namespace

Introduce:

```ts
nex.mapper
```

This namespace exposes controlled Mapper capabilities, not the graph data structure or persistence layer.

Initial conceptual API:

```ts
interface MapperApi {
  currentRoom(): Promise<RoomSnapshot | null>;

  findPath(
    destination: RoomRef,
    options?: PathOptions
  ): Promise<RoutePlan | null>;

  move(
    direction: Direction,
    options?: MoveOptions
  ): Promise<MovementResult>;
}
```

Potential future APIs such as `neighbors()` or `routeStatus()` should not be exposed unless required.

## 10. `currentRoom`

```ts
const room = await nex.mapper.currentRoom();
```

Returns an immutable DTO:

```ts
interface RoomSnapshot {
  id: string;
  name?: string;
}
```

Only include fields required for orchestration.

## 11. `findPath`

```ts
const plan = await nex.mapper.findPath({
  roomId: destinationRoomId
});
```

Conceptual types:

```ts
interface RoomRef {
  roomId: string;
}

interface RoutePlan {
  routeId: string;
  startRoomId: string;
  destinationRoomId: string;
  graphVersion: number;
  steps: readonly RouteStep[];
}

interface RouteStep {
  sequence: number;
  fromRoomId: string;
  direction: Direction;
  expectedRoomId: string;
}
```

Scripts cannot mutate the graph through the route plan.

## 12. `move`

`nex.mapper.move(direction)` is not equivalent to `nex.commands.send(direction)`.

It is a higher-level movement operation that must:

1. validate movement is currently allowed;
2. emit the movement command through central command dispatch;
3. attach Mapper provenance;
4. await semantic movement resolution;
5. return a structured movement result;
6. respect cancellation;
7. time out safely;
8. avoid direct Transport access.

# Part III: Movement Semantics

## 13. Movement Result

Movement must not be represented as a boolean.

```ts
type MovementResult =
  | MovementSucceeded
  | MovementBlocked
  | MovementUnexpected
  | MovementTimedOut
  | MovementDisconnected
  | MovementCancelled;
```

Conceptual DTOs:

```ts
interface MovementSucceeded {
  kind: "moved";
  fromRoomId: string;
  toRoomId: string;
}

interface MovementBlocked {
  kind: "blocked";
  fromRoomId: string;
  reason?: MovementFailureReason;
  message?: string;
}

interface MovementUnexpected {
  kind: "unexpected";
  expectedRoomId?: string;
  actualRoomId?: string;
}

interface MovementTimedOut {
  kind: "timeout";
}

interface MovementDisconnected {
  kind: "disconnected";
}

interface MovementCancelled {
  kind: "cancelled";
}
```

## 14. Failure Reasons

Where semantic parsing can identify a reason, expose structured values:

```text
NoExit
ClosedDoor
LockedDoor
CannotMove
CombatRestriction
StandingRequired
Unknown
```

Do not depend on exact raw MUD strings inside orchestration when the adapter can provide semantic meaning.

## 15. Semantic Movement Resolution

Movement completion should be driven by semantic events such as:

```text
room.entered
movement.blocked
connection.stateChanged
```

The Movement Coordinator correlates these events to the active movement request.

The script does not independently parse output lines to determine success.

## 16. One Movement in Flight

Permit one active Mapper movement operation per connection/session initially.

```text
send north
    |
await result
    |
confirm/reconcile room
    |
send east
```

This favors correctness and map integrity over speculative command pipelining.

# Part IV: Route Orchestration

## 17. Internal Route Script

Route execution should be implemented as a NexMUD-owned TypeScript module running on the shared runtime.

Conceptual algorithm:

```ts
async function executeRoute(destination: RoomRef): Promise<void> {
  while (true) {
    await routeControl.waitUntilRunnable();

    const current = await nex.mapper.currentRoom();

    if (!current) {
      throw new RouteStateError("Current room is unknown");
    }

    if (current.id === destination.roomId) {
      complete();
      return;
    }

    const plan = await nex.mapper.findPath(destination);

    if (!plan || plan.steps.length === 0) {
      fail("No path");
      return;
    }

    const step = plan.steps[0];
    const result = await nex.mapper.move(step.direction);

    switch (result.kind) {
      case "moved":
        continue;

      case "unexpected":
        continue;

      case "blocked":
        if (await recovery.tryRecover(step, result)) {
          continue;
        }

        pauseOrFail(result);
        return;

      case "cancelled":
      case "disconnected":
        return;

      case "timeout":
        pauseOrFail(result);
        return;
    }
  }
}
```

The exact code may differ. This is the intended boundary.

## 18. Replan Rather Than Mutate

When current state diverges from the route plan:

```text
expected room != actual room
```

the orchestrator requests a new path.

```text
unexpected room
    |
C# reconciliation
    |
current room established
    |
findPath(destination)
    |
new RoutePlan
```

Do not mutate the old plan to simulate correctness.

## 19. Graph Version

A route plan should carry a graph/version identity if the existing Mapper can support it cheaply.

The goal is simply to detect that a plan may be stale after graph mutation, not to add complex distributed versioning.

# Part V: Route Lifecycle

## 20. Route States

Recommended state machine:

```text
Idle
Planning
Running
Paused
Recovering
Replanning
Completed
Aborted
Failed
```

Transitions must be explicit and observable.

## 21. Start

Starting a route creates a route execution root scope:

```text
MapperRouteExecution
  RouteExecutionId
  Destination
  Source
  CancellationScope
  RuntimeTask
```

Only one active route should control autonomous Mapper movement per connection unless future requirements explicitly permit otherwise.

## 22. Pause

Pause means:

- do not begin a new movement step;
- retain destination and route identity;
- retain enough state to resume;
- cancel or settle pending delay/recovery work as appropriate.

If a movement command is already in flight, allow semantic resolution to finish before stable `Paused` state unless cancellation semantics make that unsafe.

## 23. Resume

Resume means:

```text
re-read current room
    |
recompute path to destination
    |
continue
```

Do not blindly continue an old step list after an arbitrary pause because the player may have moved manually.

## 24. Abort

Abort is terminal.

It must:

- cancel route-owned runtime work;
- cancel route-owned timers;
- cancel pending movement wait;
- stop automatic command emission;
- release route authority;
- emit route-aborted diagnostics/event.

Abort must not disconnect the player or disable unrelated Automation/Jev behavior.

## 25. Completion

A route is complete only when authoritative current-room state matches the destination.

Do not declare success merely because the final movement command was sent.

# Part VI: Authority and Command Arbitration

## 26. Route Authority

The Mapper route executor should hold explicit navigation authority while actively controlling movement.

```text
NavigationAuthority
  Owner = MapperRoute
  RouteExecutionId
```

This provides command-arbitration context, not Transport access.

## 27. Manual Commands During Route Execution

Manual player input remains authoritative.

Recommended initial policy for manual movement:

```text
manual movement
    -> pause route
    -> allow manual command
    -> reconcile resulting room
```

Do not fight the user by immediately moving them back onto the route.

Non-movement commands need not pause navigation unless they conflict with route execution.

## 28. Automation/Jev Movement Conflict

During this slice:

- Mapper route execution owns autonomous navigation.
- Other runtime consumers should not emit competing directional movement while a Mapper route owns authority.

Future Jev integration should request/own routes rather than issue private step commands.

Do not add Automation movement actions before conflict semantics are designed.

## 29. Command Provenance

Mapper movement commands must identify:

```text
SourceKind = Mapper
RouteExecutionId
RouteId
RouteStep
ScriptInstanceId
InvocationId
```

Example:

```text
Command: north
Source: Mapper
Route: Market Square -> South Gate
Step: 3/8
Reason: route execution
```

# Part VII: Events and Observability

## 30. Mapper Events

Expose route lifecycle as semantic events:

```text
mapper.routeStarted
mapper.routePlanned
mapper.routeStepStarted
mapper.routeStepCompleted
mapper.routeBlocked
mapper.routeReplanning
mapper.routePaused
mapper.routeResumed
mapper.routeCompleted
mapper.routeAborted
mapper.routeFailed
```

These support UI, diagnostics, future Jev, future Automation, and replay tests.

## 31. Route Diagnostics

The operational console should explain:

```text
destination selected
route planned
step started
command emitted
movement result
room reconciled
route replanned
recovery attempted
route paused
route resumed
route completed
route aborted
route failed
```

## 32. UI Status

The Mapper UI should surface at least:

```text
Destination
Route state
Current step / remaining steps
Pause
Resume
Abort
Failure/recovery reason
```

The implementation must remain non-blocking.

# Part VIII: Recovery

## 33. Recovery Is Policy

Recovery should be explicit orchestration policy, not scattered special cases inside graph/pathfinding code.

Initial cases may include:

```text
standing required
closed door
temporary movement rejection
unexpected room
```

## 34. Stand Recovery

Repeated `stand` spam must be impossible.

If movement failure indicates standing is required:

```text
attempt stand once
    |
await relevant semantic result/state
    |
retry movement once
```

Recovery must be bounded.

## 35. Door Recovery

If Avendar semantics reliably indicate a closed door and exit metadata permits opening:

```text
movement blocked: ClosedDoor
    |
send open <direction/door>
    |
await result/state
    |
retry movement
```

Game-specific command generation belongs to the adapter/domain capability where possible. Do not hard-code Avendar syntax into generic TypeScript orchestration.

## 36. Recovery Budget

Each route step needs finite recovery.

Conceptually:

```text
stand recovery: 1
door recovery: 1
movement retry: 1
replan count: bounded/configurable
```

Exact defaults may be tuned later.

# Part IX: Persistence

## 37. Route Runtime State

Active route execution is session state and should not automatically survive application restart.

Persisted map data remains separate.

## 38. Map Persistence

No script writes directly to Mapper persistence.

Room/exit discovery continues through typed C# Mapper services.

# Part X: Permissions

## 39. Mapper Capabilities

Introduce capability checks such as:

```text
mapper.read
mapper.pathfind
mapper.move
mapper.route.observe
```

Potential future capabilities:

```text
mapper.route.control
mapper.write
```

Ordinary route orchestration should not require `mapper.write`.

## 40. Initial Exposure

For this slice, `nex.mapper` may be stable for NexMUD-owned orchestration but remain preview/internal for unrestricted general-user scripting until migration and replay coverage are complete.

# Part XI: Compatibility Path Migration

## 41. Existing Mapper Workflow DSL

v0.25.0 retains Mapper/Jev workflow DSL execution on a compatibility path.

This slice removes Mapper route execution from that path.

Temporary migration:

```text
existing route request
      |
      +--> legacy Mapper workflow
      |
      +--> new scripting orchestration
```

Final state:

```text
route request
    -> Mapper domain plan
    -> shared scripting runtime
```

## 42. Jev Compatibility Path

Do not remove the Jev compatibility path in this slice unless required by the Mapper change.

If Jev requests navigation today, adapt it to call the new Mapper route-control service rather than retain a private navigation loop.

# Part XII: Replay and Determinism

## 43. Route Replay

A route must be testable without a live MUD.

```text
Given:
  current room A
  destination D
  route A -> B -> C -> D

When:
  replay supplies movement success events

Then:
  commands emitted:
    north
    east
    south

And:
  route completes in D
```

## 44. Blocked Replay

```text
A -> B -> C

step B -> C blocked
alternate path B -> E -> C exists
```

Expected:

```text
blocked
replan
B -> E -> C
complete
```

## 45. Unexpected-Room Replay

```text
expected B
actual X
```

Expected:

```text
reconcile X
find path X -> destination
continue or fail cleanly
```

## 46. Pause/Manual Movement Replay

```text
route running
manual west
```

Expected:

```text
route pauses
manual west executes
room reconciles
resume recomputes path
```

# Part XIII: Failure Model

## 47. Route Failure Reasons

Use structured reasons:

```text
NoCurrentRoom
NoPath
MovementBlocked
MovementTimeout
Disconnected
RecoveryExhausted
MapperFault
ScriptFault
Cancelled
```

Display human-readable context separately.

## 48. Script Failure

If Mapper orchestration faults:

- terminate the route safely;
- stop automatic movement;
- release route authority;
- emit diagnostics;
- leave connection, UI, Automation, and other scripts running.

## 49. Mapper Domain Failure

If C# pathfinding or reconciliation fails:

- return a structured host error;
- fault or pause according to policy;
- do not leak raw host exceptions into TypeScript;
- record full host diagnostics internally.

# Part XIV: Tests

## 50. Domain Tests

Test:

- route-plan generation;
- pathfinding;
- graph changes;
- room reconciliation;
- destination resolution.

These remain C# domain tests independent from Jint.

## 51. Mapper SDK Tests

Test:

- `currentRoom`;
- `findPath`;
- `move`;
- capability enforcement;
- DTO isolation;
- cancellation;
- host errors;
- no direct Mapper repository exposure.

## 52. Orchestration Tests

Test:

- normal multi-step route;
- pause;
- resume;
- abort;
- disconnect;
- no path;
- blocked movement;
- unexpected room;
- movement timeout;
- bounded recovery;
- replan;
- completion only after room confirmation;
- manual movement authority;
- no concurrent movement commands.

## 53. Provenance Tests

Verify every movement command contains:

```text
SourceKind = Mapper
RouteExecutionId
RouteStep
```

and still passes through central command dispatch.

## 54. UI Responsiveness Test

Run a long route and confirm:

- map remains interactive;
- world output continues;
- input remains usable;
- pause/abort respond promptly;
- no synchronous wait occurs on the UI thread.

This directly guards against the historical Mapper beach-ball behavior.

# Part XV: Acceptance Criteria

## 55. Architecture Acceptance

The slice is complete when:

- graph/pathfinding remain typed C# domain logic;
- route execution runs through the shared scripting runtime;
- `nex.mapper.currentRoom()` works;
- `nex.mapper.findPath()` works;
- `nex.mapper.move()` returns structured semantic results;
- only one Mapper movement is in flight per connection;
- route execution is non-blocking;
- pause works;
- resume recomputes from current authoritative room;
- abort deterministically cancels route-owned work;
- unexpected rooms cause reconciliation and replan;
- blocked movement follows bounded recovery policy;
- repeated `stand` spam is impossible;
- route completion requires destination confirmation;
- manual movement pauses autonomous route control;
- Mapper commands use central dispatch;
- Mapper provenance is preserved;
- route lifecycle is observable;
- replay tests cover success, blocking, replan, pause/resume, and abort;
- Mapper route execution no longer depends on the legacy Mapper workflow compatibility path;
- no raw Jint types leak into Mapper domain code;
- no raw Mapper repositories or SQLite handles are exposed to scripts.

# Part XVI: Explicit Stop Point

After Mapper orchestration is accepted, stop before expanding generalized user Mapper scripting.

The next architecture task is:

```text
NexMUD Jev Execution Migration Architecture
```

That slice should define:

- Jev intent/plan representation;
- separation of reasoning from execution;
- plan ownership;
- cancellation via the global Jev toggle;
- use of Mapper routes rather than private navigation;
- deterministic behaviors graduating from Jev into Automation;
- command provenance;
- recovery and safety boundaries;
- compatibility-path removal.

## 56. Roadmap

```text
Jint Integration
      |
      v
Scripting Vertical Slice
      |
      v
SDK + Automation Migration
      |
      v
THIS SLICE:
Mapper Domain API
+ nex.mapper
+ Movement Coordinator
+ Route Orchestration
+ Pause / Resume / Abort
+ Recovery / Replan
+ Mapper Compatibility Removal
      |
      v
Jev Execution Migration
      |
      v
User Scripting UX
      |
      v
Plugin / Extensibility Model
```

## 57. Final Architectural Rule

The Mapper must not become a workflow engine.

It should answer:

```text
Where am I?
What does the map know?
How do I get there?
What happened when movement was attempted?
```

The scripting platform should answer:

```text
What do we do next?
When do we retry?
When do we pause?
When do we replan?
When are we finished?
```

That separation keeps Mapper algorithms deterministic, route behavior observable, and future Jev/Automation integration on one shared execution model.
