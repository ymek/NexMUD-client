# NexMUD Scripting Vertical Slice Architecture

**Status:** Proposed implementation architecture  
**Date:** 2026-09-26  
**Product:** NexMUD  
**Subsystem:** Jev remains the intelligent decision/agent subsystem within NexMUD  
**Depends on:** NexMUD Scripting Platform Architecture; NexMUD Jint Integration Architecture

## 1. Decision

The next implementation slice is:

```text
Semantic Event
    |
    v
Script Event Subscription
    |
    v
TypeScript Handler
    |
    v
Jint Runtime
    |
    v
NexMUD Host SDK
    |
    v
Command Dispatch / Arbitration
    |
    v
Transport
```

The canonical proof path uses a low-complexity event-to-command workflow:

```text
CharacterVitalsChanged
        |
        v
TypeScript event handler
        |
        v
conditional policy
        |
        v
nex.commands.send(...)
        |
        v
central command dispatcher
        |
        v
transport
```

The purpose is not the usefulness of the example automation itself. The purpose is to prove the complete architecture end-to-end with semantic event delivery, TypeScript compilation, Jint execution, public SDK usage, async host calls, capability enforcement, command provenance, cancellation, diagnostics, source mapping, replayability, and UI non-blocking behavior.

No major subsystem migration begins until this vertical slice is stable.

## 2. Goals

This slice must prove that:

- Jint is usable as a real NexMUD execution substrate.
- Scripts can consume semantic NexMUD events.
- TypeScript authors interact only with the public NexMUD SDK.
- Host calls cross a narrow capability-controlled boundary.
- Script-originated commands use the existing central command path.
- Script work never blocks the UI thread.
- All engine entry remains serialized per script instance.
- Script activity has traceable provenance.
- Script cancellation is deterministic.
- Runtime failures are isolated.
- TypeScript source locations appear in diagnostics.
- The same behavior can be driven by replayed events.
- The first SDK contract is coherent enough to stabilize before broader migration.

## 3. Non-Goals

This slice does not yet:

- replace the existing Automation subsystem;
- move mapper route orchestration into scripts;
- move Jev execution into scripts;
- expose a general user-facing script editor;
- provide a plugin marketplace or plugin packaging system;
- add npm support;
- expose direct SQLite access;
- expose raw CLR objects;
- support arbitrary filesystem access;
- support arbitrary network access;
- define the final complete SDK.

The SDK defined here is intentionally minimal.

## 4. Architectural Shape

```text
+------------------------------------------------------+
|                    NexMUD Core                       |
|                                                      |
|  Protocols -> Semantic Events -> Core State          |
+--------------------------+---------------------------+
                           |
                           v
+------------------------------------------------------+
|              Script Subscription Router              |
|                                                      |
|  filters events by script subscription               |
|  converts host event -> immutable script DTO         |
+--------------------------+---------------------------+
                           |
                           v
+------------------------------------------------------+
|             Script Instance / Mailbox                |
|                                                      |
|  serialized work queue                               |
|  cancellation scope                                  |
|  diagnostics context                                 |
+--------------------------+---------------------------+
                           |
                           v
+------------------------------------------------------+
|                      Jint                            |
|                                                      |
|  compiled JavaScript module                          |
|  TypeScript-derived handler                          |
+--------------------------+---------------------------+
                           |
                           v
+------------------------------------------------------+
|                NexMUD Script SDK                     |
|                                                      |
|  nex.events                                          |
|  nex.commands                                        |
|  nex.state                                           |
|  nex.log                                             |
+--------------------------+---------------------------+
                           |
                           v
+------------------------------------------------------+
|                 Host Capability API                  |
|                                                      |
|  permission checks                                   |
|  async request bridge                                |
|  DTO conversion                                      |
|  provenance creation                                 |
+--------------------------+---------------------------+
                           |
                           v
+------------------------------------------------------+
|            Central Command Dispatcher                |
|                                                      |
|  arbitration                                         |
|  ordering                                            |
|  source identity                                     |
|  cancellation                                        |
+--------------------------+---------------------------+
                           |
                           v
+------------------------------------------------------+
|                    Transport                         |
+------------------------------------------------------+
```

The critical rule remains:

> Scripts orchestrate NexMUD capabilities. They do not bypass NexMUD architecture.

## 5. Public SDK Naming

The public script API should use the product name, not the intelligent subsystem name.

Recommended:

```ts
nex.events
nex.commands
nex.state
nex.log
```

Not:

```ts
jev.events
jev.commands
```

Jev is one consumer of the scripting platform. The scripting platform belongs to NexMUD.

Conceptually:

```ts
declare const nex: NexMudApi;
```

Jev-specific functions, when eventually exposed, belong under a dedicated namespace:

```ts
nex.jev
```

rather than making `jev` the root application API.

## 6. Initial SDK Surface

### 6.1 `nex.events`

Responsibilities:

- subscribe to supported semantic events;
- unsubscribe;
- expose typed event payloads.

Example:

```ts
const subscription = nex.events.on(
  "character.vitalsChanged",
  async event => {
    // policy
  }
);

subscription.dispose();
```

Initial event set should contain only events needed for the vertical slice and one or two supporting tests.

Recommended first event:

```text
character.vitalsChanged
```

Optional supporting event:

```text
connection.stateChanged
```

Do not expose every Core event immediately.

### 6.2 `nex.commands`

Responsibilities:

- send a command through central command dispatch;
- preserve source provenance;
- return a structured result;
- respect cancellation.

Example:

```ts
await nex.commands.send("flee");
```

Proposed contract:

```ts
interface CommandsApi {
  send(command: string): Promise<CommandResult>;
}

interface CommandResult {
  accepted: boolean;
  commandId: string;
}
```

The script API does not expose Transport directly.

### 6.3 `nex.state`

Responsibilities:

- provide immutable snapshots of selected current state;
- avoid live CLR object exposure.

For the first slice:

```ts
interface StateApi {
  readonly character: CharacterStateSnapshot;
}

interface CharacterStateSnapshot {
  readonly health: ResourceState;
  readonly mana: ResourceState;
  readonly movement: ResourceState;
}

interface ResourceState {
  readonly current: number;
  readonly maximum: number;
  readonly percent: number;
}
```

The event payload should contain sufficient information to avoid forcing state reads where unnecessary, but `nex.state` proves the state-query surface.

### 6.4 `nex.log`

Responsibilities:

- structured script diagnostics;
- script identity automatically attached;
- no direct application logger exposure.

Example:

```ts
nex.log.info("Low health policy activated", {
  healthPercent: event.health.percent
});
```

Initial methods:

```ts
debug(...)
info(...)
warn(...)
error(...)
```

## 7. Event Contract

The semantic event bus remains the source of truth.

Scripts do not subscribe directly to Protocols, UI events, or transport output for this slice.

Flow:

```text
Protocol input
    |
    v
existing parser / adapter
    |
    v
semantic event
    |
    v
core state reduction
    |
    v
script event router
    |
    v
immutable script DTO
```

The script-facing event name and payload are API contracts, not direct projections of internal C# event classes.

Example:

```ts
interface CharacterVitalsChangedEvent {
  readonly health: ResourceState;
  readonly mana: ResourceState;
  readonly movement: ResourceState;
  readonly timestamp: number;
}
```

This decouples the public scripting API from future Core refactors.

## 8. Event Naming Convention

Use stable, product-facing event identifiers.

Recommended convention:

```text
domain.eventName
```

Examples:

```text
character.vitalsChanged
character.levelChanged
room.entered
combat.started
combat.ended
mapper.routeStarted
mapper.routeCompleted
connection.stateChanged
```

C# type names must not become the external event identifier.

The script-facing identifier is a versioned API contract.

## 9. Script Package for the Vertical Slice

Create one internal reference script package.

Recommended identity:

```text
nexmud.reference.vitals-policy
```

Suggested structure:

```text
scripts/
  reference/
    vitals-policy/
      manifest.json
      main.ts
```

Example manifest:

```json
{
  "id": "nexmud.reference.vitals-policy",
  "version": "1.0.0",
  "apiVersion": "1",
  "entrypoint": "main.ts",
  "permissions": [
    "events.subscribe",
    "state.read",
    "commands.send",
    "log.write"
  ]
}
```

Example implementation:

```ts
export function activate(): void {
  nex.events.on("character.vitalsChanged", async event => {
    if (event.health.percent >= 25) {
      return;
    }

    nex.log.warn("Health threshold reached", {
      healthPercent: event.health.percent
    });

    await nex.commands.send("flee");
  });
}
```

This script exists as an architectural test fixture and reference implementation. It is not intended to become hard-coded gameplay policy.

## 10. Script Registration

Script activation must result in explicit registration.

Conceptually:

```text
compile package
    |
    v
create script instance
    |
    v
load module
    |
    v
activate()
    |
    v
register event handler
    |
    v
subscription owned by script instance
```

The runtime must know:

```text
script instance
    -> registered subscriptions
    -> pending host requests
    -> timers
    -> current invocations
```

Stopping the script disposes its subscriptions automatically.

A script must not be able to leave orphan event handlers behind after reload or failure.

## 11. Invocation Model

Each event handler invocation receives an execution identity.

Recommended:

```text
ScriptInstanceId
InvocationId
EventId
CorrelationId
CancellationScope
```

Flow:

```text
semantic event
    |
    v
subscription match
    |
    v
create ScriptInvocation
    |
    v
enqueue work
    |
    v
execute handler
    |
    +--> host operations
    |
    v
complete / fault / cancel
```

The invocation is the unit of diagnostics and cancellation.

## 12. Script Mailbox

Every script instance must retain the Jint architecture's serialized mailbox rule.

```text
event A ----\
event B -----+--> Script Mailbox --> Jint
host result -/
cancel ------/
```

No event handler enters Jint directly from the Core event bus.

No host task continuation enters Jint directly.

This protects UI responsiveness, engine consistency, deterministic ordering, and lifecycle safety.

For this slice, FIFO execution is sufficient unless a more specific contract already exists.

## 13. Async Command Bridge

`nex.commands.send()` is the first production async host operation.

Flow:

```text
TypeScript:
await nex.commands.send("flee")
        |
        v
JS bootstrap
        |
        v
__nexHost.begin(
    "commands.send",
    requestId,
    { command: "flee" }
)
        |
        v
C# host router
        |
        v
permission check
        |
        v
command dispatcher
        |
        v
CommandResult
        |
        v
post completion to script mailbox
        |
        v
resolve Promise inside Jint
```

The command dispatcher completion must not directly invoke the Jint engine. It posts completion back to the owning script mailbox.

## 14. Command Provenance

Every command emitted through the scripting layer must preserve origin.

Recommended model:

```text
CommandSource
  SourceKind
  ScriptId
  ScriptVersion
  ScriptInstanceId
  InvocationId
  EventId
  ParentOperationId
```

For the reference script:

```text
SourceKind = Script
ScriptId   = nexmud.reference.vitals-policy
```

Later:

```text
SourceKind = Automation
SourceKind = Jev
SourceKind = Mapper
```

even though those systems may execute through the same Jint runtime.

This distinction belongs in command metadata, not separate transport paths.

## 15. Command Arbitration

This slice must confirm that scripting does not create another command path.

All script commands enter the same arbitration path used by existing NexMUD-generated commands.

At minimum the dispatcher must support:

- ordering;
- provenance;
- cancellation;
- rejection;
- logging.

If existing arbitration logic is incomplete, this slice should expose the gap rather than bypass it.

The vertical slice is not complete if the easiest implementation is:

```text
script -> transport.SendAsync(...)
```

That is explicitly prohibited.

## 16. Cancellation

Cancellation must be proven end-to-end.

### Script stop

```text
stop script
  -> stop accepting new events
  -> cancel active invocations
  -> cancel pending host requests
  -> dispose subscriptions
  -> dispose engine
```

### Application disconnect

Pending command operations tied to the disconnected session must complete as cancelled or rejected.

### Reload

Old instance requests must not resolve into the replacement engine.

### Invocation cancellation

If an invocation is cancelled before `nex.commands.send()` completes, its eventual host completion must be ignored or translated into a cancellation result according to the host bridge contract.

## 17. Capability Enforcement

The public SDK is not the security boundary. The C# host router is.

For this slice:

```text
nex.events.on(...)
    -> events.subscribe

nex.state.*
    -> state.read

nex.commands.send(...)
    -> commands.send

nex.log.*
    -> log.write
```

Every host operation performs capability validation against the script's resolved permission set.

Generated internal scripts may use a trusted manifest profile, but they still use the same host-operation route.

Do not introduce bypass APIs for internal scripts.

## 18. TypeScript SDK Declarations

The slice should establish the first real SDK declaration set.

Recommended package identity:

```text
@nexmud/api
```

This may initially be an internal declaration bundle rather than a published npm package.

Example:

```ts
declare const nex: NexMudApi;

interface NexMudApi {
  readonly events: EventsApi;
  readonly commands: CommandsApi;
  readonly state: StateApi;
  readonly log: LogApi;
}
```

The reference TypeScript script must compile exclusively against this declaration surface.

No use of Jint, CLR types, implementation bridge types, or application internals is permitted.

## 19. SDK Version 1 Boundary

This slice establishes the beginning of Script API version `1`.

Version 1 does not need to be feature-complete. It does need to be internally coherent.

Initial contract:

```text
@nexmud/api v1

nex.events
nex.commands
nex.state
nex.log
```

The following are reserved for subsequent slices:

```text
nex.timers
nex.storage
nex.mapper
nex.codex
nex.ui
nex.jev
```

Do not prematurely finalize their method signatures.

## 20. Diagnostics

The first slice must produce useful traceability.

Recommended diagnostic sequence:

```text
[script] loaded nexmud.reference.vitals-policy@1.0.0
[script] subscribed character.vitalsChanged
[script] invocation started
[script] health threshold reached: 23%
[script] host request commands.send started
[command] "flee" source=Script:nexmud.reference.vitals-policy
[script] host request commands.send completed
[script] invocation completed
```

Errors should show original TypeScript locations where source maps allow it.

Example:

```text
nexmud.reference.vitals-policy/main.ts:14:11
```

not only compiled JavaScript offsets.

## 21. UI Thread Safety

The slice must explicitly test that:

- script compilation does not occur on the UI thread;
- Jint event handling does not occur on the UI thread;
- host operations do not block the UI thread;
- diagnostics do not synchronously stall the UI;
- command dispatch remains asynchronous.

A deliberately slow or looping script must not freeze Avalonia.

The Jint resource limits remain the final guard against runaway execution.

## 22. Replay

The same semantic event should be injectable from replay infrastructure.

Test shape:

```text
Given:
  reference vitals script is active

When:
  replay publishes character.vitalsChanged
  with health.percent = 20

Then:
  exactly one command is requested:
  "flee"
```

The test must not require a live MUD connection, Avalonia UI, or actual transport write.

Command dispatch may terminate at a test sink.

This becomes the first proof that scripting behavior can be validated independently from live gameplay.

## 23. Determinism

The reference behavior must not rely on:

- wall clock;
- random values;
- filesystem;
- network;
- external process state.

Given:

```text
same compiled script
same event
same initial state
same API version
```

the script should make the same policy decision.

This forms the basis for later Automation and Jev regression testing.

## 24. Fault Isolation

Required test cases:

### Handler exception

```ts
throw new Error("test");
```

Expected:

- invocation faults;
- script diagnostic emitted;
- Core event bus continues;
- other scripts continue;
- UI continues;
- application remains connected.

### Infinite loop

Expected:

- Jint execution limit terminates invocation;
- script is marked faulted according to configured policy;
- UI remains responsive.

### Permission denial

A script lacking `commands.send` attempts:

```ts
await nex.commands.send("flee");
```

Expected:

- structured permission error;
- no command emitted;
- diagnostic records script and invocation identity.

### Late async result after unload

Expected:

- completion discarded;
- no re-entry into disposed Jint engine.

## 25. Observability Model

This slice should establish a common script status model reusable later by Automation, Mapper, and Jev.

Recommended:

```text
ScriptStatus
  Disabled
  Loading
  Running
  Faulted
  Stopping
```

Invocation status:

```text
Queued
Running
WaitingOnHost
Completed
Faulted
Cancelled
```

These states should be available to the existing operational/debug console.

A dedicated end-user scripting UI is not required yet.

## 26. Reference Script Ownership

The reference vertical-slice script should be classified as an internal development/reference script.

It should not automatically run during normal gameplay in release builds unless explicitly enabled.

Recommended usage:

- integration tests;
- replay tests;
- debug/development builds;
- architecture verification.

The vertical slice tests the mechanism, not a permanent health automation policy.

## 27. Tests

### SDK compilation tests

Verify:

- valid script compiles;
- invalid event name fails type checking where possible;
- invalid event payload use fails compilation;
- `nex.commands.send` has the correct Promise type;
- unsupported host APIs are absent.

### Event routing tests

Verify:

- subscribed script receives event;
- unsubscribed script does not;
- event payload is correct;
- event order is preserved;
- script unload removes subscriptions.

### Jint execution tests

Verify:

- handler executes;
- one engine remains serialized;
- independent scripts remain isolated;
- handler faults do not escape runtime boundary.

### Host bridge tests

Verify:

- permission check occurs;
- payload validation occurs;
- async completion returns through mailbox;
- cancelled request does not resolve into a dead engine.

### Command tests

Verify:

- command reaches central dispatcher;
- transport is not called directly by the scripting adapter;
- provenance is correct;
- rejected commands return structured result;
- duplicate invocation does not occur from one event.

### Replay tests

Verify:

- replayed event invokes the same script;
- the same event produces the same command decision;
- the test runs without a live network connection.

### UI responsiveness test

Run a constrained pathological script and verify the UI/event-processing path remains responsive.

## 28. Acceptance Criteria

This slice is complete when all of the following are true:

- a real TypeScript script compiles through the NexMUD script compiler;
- the compiled JavaScript loads into Jint;
- the script registers a typed semantic-event handler;
- a Core semantic event is routed to the script through the script mailbox;
- the event handler executes without UI-thread involvement;
- the script can read a typed state snapshot;
- the script can write structured diagnostics;
- the script can asynchronously request a command;
- the command is capability checked;
- the command enters the existing central command dispatcher;
- command provenance identifies the originating script and invocation;
- completion returns to Jint only through the serialized mailbox;
- cancellation works during active host work;
- unload removes subscriptions and prevents late completion re-entry;
- an exception in the script does not affect Core, Transport, UI, or another script;
- a runaway script is constrained;
- TypeScript source locations appear in runtime diagnostics where possible;
- the same behavior works under replay without a live MUD connection;
- no Jint type leaks outside the Jint adapter project;
- no raw CLR/domain object is exposed to TypeScript;
- no alternate event bus, scheduler, or transport path is introduced.

## 29. Explicit Stop Point

After this slice is accepted, do not immediately expand arbitrary user scripting.

The next architectural task is to stabilize the shared SDK surfaces required by the first subsystem migration.

Expected next slice:

```text
nex.timers
nex.storage
Automation compiler
```

followed by migration of a small existing Automation behavior onto the runtime.

Mapper-specific SDK design should follow only after Automation proves the generalized orchestration model.

## 30. Subsequent Roadmap

```text
Jint Integration
      |
      v
THIS SLICE:
Event -> TypeScript -> Jint -> Host -> Command
      |
      v
SDK stabilization
events / commands / state / log / timers / storage
      |
      v
Automation compiler migration
      |
      v
Mapper orchestration API + route execution migration
      |
      v
Jev plan execution migration
      |
      v
User scripting UX
      |
      v
Plugin/extensibility layer
```

## 31. Final Architectural Rule

The purpose of this slice is not to prove that Jint can run JavaScript. That has already been established by the Jint integration.

The purpose is to prove:

> NexMUD can execute real application behavior through one controlled, typed, observable scripting path without bypassing the existing domain architecture.

Once this path is trusted, Automation, Mapper orchestration, Jev execution, and eventually user extensions can converge on it rather than continuing to build independent execution machinery.
