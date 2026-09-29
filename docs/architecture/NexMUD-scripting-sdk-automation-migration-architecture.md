# NexMUD Scripting SDK and Automation Migration Architecture

**Status:** Proposed implementation architecture  
**Date:** 2026-09-27  
**Product:** NexMUD  
**Subsystem:** Jev remains the intelligent decision/agent subsystem within NexMUD  
**Depends on:** NexMUD Scripting Platform Architecture; NexMUD Jint Integration Architecture; NexMUD Scripting Vertical Slice Architecture

**Scope:** Stabilize the first durable NexMUD scripting SDK and migrate the existing Automation subsystem from an independent executor into a declarative compiler/runtime client of the shared scripting platform.

## 1. Decision

The next architectural milestone has two tightly coupled goals:

1. Stabilize the minimum SDK surface needed by generalized automation.
2. Move Automation execution onto the shared scripting runtime.

The target architecture is:

```text
Automation UI / Config
        |
        v
Automation Definition
        |
        v
Automation AST / IR
        |
        v
Automation Compiler
        |
        v
Generated JavaScript
        |
        v
Jint Runtime
        |
        v
NexMUD Script SDK
        |
        v
Host Capability API
        |
        v
Existing NexMUD Domain Services
```

Automation remains a first-class user feature. It stops being a separate execution engine.

## 2. Architectural Principle

> Automation owns declarative intent. The scripting platform owns execution.

Automation continues to own concepts such as aliases, triggers, timers, conditions, actions, enabled state, grouping, ordering, and user configuration.

Automation should no longer independently own event-loop semantics, runtime scheduling, timer implementation, cancellation machinery, command dispatch, command provenance, script-like persistence, error isolation, host-operation lifecycle, or execution diagnostics.

## 3. Goals

This slice must:

- establish a stable SDK v1 baseline for automation;
- add `nex.timers`;
- add `nex.storage`;
- preserve the existing user-facing Automation concepts;
- compile Automation definitions into runtime-executable JavaScript;
- execute generated Automation through Jint;
- eliminate duplicate Automation execution machinery incrementally;
- preserve command provenance as `Automation`, not generic `Script`;
- preserve enable/disable semantics;
- support deterministic cancellation;
- support replay tests;
- preserve existing persistence semantics or migrate them explicitly;
- allow future UI improvements without coupling UI directly to Jint;
- define a clean Automation IR so future compilers or runtime engines remain replaceable.

## 4. Non-Goals

This slice does not yet migrate mapper route orchestration, migrate Jev execution, expose `nex.mapper`, expose `nex.codex`, expose `nex.ui`, expose `nex.jev`, create a plugin framework, add npm support, allow arbitrary JavaScript inside every Automation field, or redesign the entire Automation UI.

Migration should be incremental and testable.

# Part I: SDK Stabilization

## 5. SDK v1 Baseline

After this slice, the durable SDK v1 baseline is:

```text
nex.events
nex.commands
nex.state
nex.log
nex.timers
nex.storage
```

These six namespaces become the common behavioral substrate for Automation, future Mapper orchestration, future Jev execution, and user-authored scripts.

The API must remain intentionally small.

## 6. `nex.events`

The vertical slice already proves semantic event subscriptions. This slice stabilizes event registration behavior needed by Automation.

Required conceptual API:

```ts
nex.events.on(eventName, handler)
nex.events.off(subscription)
```

Required properties:

- subscription ownership by script instance;
- automatic cleanup on unload;
- deterministic registration order;
- immutable event DTOs;
- stable external event names;
- no exposure of raw CLR event types.

Automation compiler output uses the same event API as user scripts.

## 7. `nex.commands`

`nex.commands` remains the only supported command-emission path from scripts and compiled Automation.

Required:

```ts
await nex.commands.send(command)
```

Alias expansion should preserve ordering through the existing command dispatcher. Internally prefer structured command sequences over separator-delimited strings.

Command metadata from Automation must identify:

```text
SourceKind = Automation
AutomationId
AutomationType
ScriptInstanceId
InvocationId
EventId / TriggerId
```

Generated Automation must not appear merely as generic `Script` provenance.

## 8. `nex.state`

The existing read-only state snapshot model remains.

Automation conditions may consume stable state DTOs, for example:

```ts
nex.state.character.health.percent
nex.state.character.mana.percent
nex.state.room.id
nex.state.combat.active
```

Only expose fields required by real automation use cases. Do not mirror the entire Core state tree into TypeScript.

## 9. `nex.log`

Generated Automation may log diagnostics through:

```ts
nex.log.debug(...)
nex.log.info(...)
nex.log.warn(...)
nex.log.error(...)
```

The Automation runtime should automatically attach `automationId`, `automationType`, `automationName`, and `invocationId`.

## 10. `nex.timers`

Timers must be owned by the NexMUD runtime, not by Jint globals.

Recommended initial API:

```ts
interface TimersApi {
  delay(ms: number): Promise<void>;
  after(ms: number, callback: TimerCallback): TimerHandle;
  every(ms: number, callback: TimerCallback): TimerHandle;
  cancel(handle: TimerHandle): void;
}
```

Every timer belongs to a script/automation runtime owner. Stopping or disabling its owner cancels the timer.

Timers use the NexMUD clock/scheduler abstraction. Do not rely directly on `setTimeout` or `setInterval` unless those functions are explicitly shims over the NexMUD scheduler.

Timers must operate against virtual/replay time where supported.

Recurring timers should default to **fixed delay** semantics: the interval begins after the previous callback completes. This avoids callback pile-up.

## 11. `nex.storage`

Recommended initial API:

```ts
interface StorageApi {
  get<T>(key: string): Promise<T | null>;
  set<T>(key: string, value: T): Promise<void>;
  delete(key: string): Promise<void>;
  has(key: string): Promise<boolean>;
}
```

Storage is namespaced by owner identity:

```text
script:{scriptId}
automation:{automationId}
```

Values must be JSON-serializable. Scripts receive no database handle and no SQL capability.

Compiled/runtime state is distinct from persisted Automation definitions.

# Part II: Automation Domain Model

## 12. Automation Remains a Typed Domain

Automation should remain a typed C# domain model.

Recommended conceptual hierarchy:

```text
AutomationDefinition
  Id
  Name
  Enabled
  Type
  Trigger
  Conditions[]
  Actions[]
  Metadata
```

Specialized definitions may include:

```text
AliasDefinition
TriggerDefinition
TimerDefinition
```

Generated JavaScript is an implementation artifact, never the source of truth.

## 13. Automation Intermediate Representation

Introduce an explicit Automation IR between UI/configuration and JavaScript generation:

```text
UI / Persistence Model
        |
        v
AutomationDefinition
        |
        v
Validated Automation IR
        |
        v
JavaScript Compiler
```

Example conceptual IR:

```text
AutomationProgram
  Trigger
    EventTrigger
    TextTrigger
    AliasTrigger
    TimerTrigger

  Conditions
    StateComparison
    EventFieldComparison
    RegexMatch
    LogicalAnd
    LogicalOr
    LogicalNot

  Actions
    SendCommand
    SendCommands
    Delay
    SetStorage
    DeleteStorage
    Log
```

The IR should be serializable and independently testable.

## 14. Why an IR Matters

Avoid:

```text
UI -> string templates -> JavaScript
```

An explicit IR provides validation before code generation, deterministic output, better security, easier debugging, stable UI evolution, replayability, and future portability to alternate runtimes.

The compiler accepts validated IR, not raw UI controls.

# Part III: Initial Automation Types

## 15. Alias Automation

Alias is the simplest migration target.

Example:

```text
Alias: heal
Actions:
  send "quaff potion"
  send "eat herb"
```

Compiled behavior conceptually subscribes to the NexMUD input-command event and consumes the original command if matched.

The exact input event name may follow existing input architecture, but alias parsing must occur before unmatched input reaches Transport.

### Alias parameters

Support structured captures rather than arbitrary JavaScript interpolation.

Example:

```text
Pattern: kk {target}
Input:   kk guard
```

produces:

```json
{
  "target": "guard"
}
```

Actions may reference `{target}` through Automation interpolation rules.

Do not evaluate arbitrary JavaScript expressions inside templates.

## 16. Trigger Automation

Two trigger categories remain distinct.

### Semantic triggers

Preferred where supported:

```text
combat.started
room.entered
character.vitalsChanged
mob.observed
```

These compile directly to `nex.events` subscriptions.

### Text triggers

Required for unsupported or game-specific textual behavior.

Pattern types may include:

```text
substring
glob
regex
```

Text triggers should not replace semantic events where semantic events already exist.

Text matching should occur in C# before Jint invocation where practical. Relevant captures are passed into the generated handler as DTO data.

Regex evaluation must use explicit timeout controls.

## 17. Timer Automation

Timer Automation compiles directly to `nex.timers`.

Example:

```text
Every 30 seconds:
  send "score"
```

conceptually compiles to:

```ts
nex.timers.every(30_000, async () => {
  await nex.commands.send("score");
});
```

Disabling the Automation cancels the timer immediately. Reloading must never create duplicate timers.

This slice implements runtime timers only unless current NexMUD behavior explicitly promises restart-persistent scheduling.

# Part IV: Conditions and Actions

## 18. Conditions Are Declarative

Conditions should not initially be arbitrary JavaScript.

Recommended condition primitives:

```text
state comparison
event field comparison
regex match
contains
logical AND
logical OR
logical NOT
storage value comparison
```

Example:

```text
health.percent < 25
AND
combat.active == true
```

The IR can represent this structurally and compile it to JavaScript.

Do not make JavaScript itself the persisted Automation expression language unless deliberately chosen later.

## 19. Initial Action Set

Recommended initial actions:

```text
SendCommand
SendCommands
Delay
Log
SetStorage
DeleteStorage
```

Potential later actions:

```text
EnableAutomation
DisableAutomation
EmitNotification
MapperMove
MapperNavigate
RunScript
JevRequest
```

Do not introduce mapper or Jev actions in this slice.

# Part V: Compilation

## 20. Automation Compiler

Introduce a compiler boundary conceptually equivalent to:

```text
IAutomationCompiler
```

Input:

```text
AutomationProgram
ScriptApiVersion
```

Output:

```text
JavaScript source
source/IR mapping metadata
manifest
permissions
content hash
diagnostics
```

The compiler never executes generated code.

## 21. Deterministic Code Generation

Given the same Automation IR, SDK version, and compiler version, generated JavaScript should be deterministic.

Benefits include cacheability, reproducible tests, diffability, easier diagnostics, and replay confidence.

Generated source may contain comments mapping statements back to Automation nodes.

## 22. Generated Code Is Not User Source

Generated JavaScript is an implementation detail.

The UI should display Automation concepts by default. Development diagnostics may expose generated source through an advanced view.

Runtime failures should map back to Automation identity and IR node where possible, for example:

```text
Automation: Emergency flee
Condition: 1
Action: SendCommand[0]
```

rather than only a generated JavaScript line number.

# Part VI: Permissions

## 23. Permission Derivation

Automation permissions should be derived from its IR.

Example:

```text
Trigger: character.vitalsChanged
Action: SendCommand
Action: SetStorage
```

produces:

```text
events.subscribe
commands.send
storage.write
```

Normal Automation definitions should not require users to manually manage permissions.

Generated internal Automation still uses the same capability checks as other scripts. Trusted origin does not create a bypass path.

# Part VII: Lifecycle

## 24. Automation Lifecycle

Recommended states:

```text
Disabled
Compiling
Loading
Running
Faulted
Stopping
```

Enable flow:

```text
definition enabled
    |
    v
validate
    |
    v
compile
    |
    v
load generated module
    |
    v
activate
    |
    v
Running
```

Disable flow:

```text
Running
    |
    v
stop accepting events
    |
    v
cancel invocations
    |
    v
cancel timers
    |
    v
cancel host requests
    |
    v
dispose subscriptions
    |
    v
Disabled
```

## 25. Hot Update

Editing an enabled Automation uses replacement semantics:

```text
old definition running
        |
new definition saved
        |
        v
validate + compile new
        |
        v
load new instance
        |
        v
atomic swap
        |
        v
stop old instance
```

If validation or compilation fails, preserve the old running definition unless the user explicitly disables it.

# Part VIII: Runtime Grouping

## 26. Automation Runtime Scope

Do not create one Jint engine per alias or trigger.

Recommended initial grouping:

```text
one Automation runtime engine per active character/session profile
```

Each Automation remains independently managed through module/subscription ownership inside that runtime scope.

A single handler fault must not disable unrelated Automation definitions.

If the engine itself becomes unsafe or corrupted, rebuild the Automation runtime scope and reload enabled definitions.

Arbitrary user-authored script packages remain independently isolated according to the Jint integration architecture.

This distinguishes trusted generated Automation from arbitrary user scripts without creating a separate execution model.

# Part IX: Migration Strategy

## 27. Strangler Migration

Do not replace the current Automation subsystem in one rewrite.

Temporarily support:

```text
AutomationDefinition
        |
        +--> Legacy Executor
        |
        +--> Script Compiler / Runtime
```

A feature flag or per-type implementation path may select the executor during migration.

The final state removes the legacy executor.

## 28. Migration Order

Recommended order:

1. aliases;
2. simple semantic triggers;
3. text triggers;
4. one-shot timers;
5. recurring timers;
6. conditions;
7. multi-action workflows;
8. persistent Automation state;
9. remove legacy execution paths.

Aliases and timers are intentionally early because they prove different execution modes.

## 29. Behavioral Parity

For each migrated type, create parity tests:

```text
same definition
same input/event/time
legacy output
new runtime output
```

Compare command ordering, match behavior, enable/disable semantics, timer semantics, cancellation, and emitted commands.

Any deliberate behavior change must be documented rather than silently accepted.

# Part X: Input and Trigger Semantics

## 30. Alias Matching Pipeline

Recommended:

```text
user input
    |
    v
input parser
    |
    v
alias matcher
    |
    +--> matched -> compiled Automation handler
    |
    +--> unmatched -> normal command dispatch
```

If an alias consumes input, the original alias command should not reach the MUD unless passthrough is explicitly configured.

Existing configurable command separator support remains independent. Alias expansion should preferably operate on structured command lists rather than concatenated separator-delimited text.

## 31. Text Trigger Matching Pipeline

Recommended:

```text
output line
    |
    v
C# trigger matcher
    |
    v
matching Automation IDs + captures
    |
    v
script mailbox
    |
    v
generated handler
```

This avoids invoking Jint for every output line for every trigger.

The Automation domain owns matching semantics. The scripting runtime owns action execution.

# Part XI: Observability

## 32. Automation Diagnostics

The existing operational console should be able to show:

```text
Automation loaded
Automation enabled
Automation disabled
Alias matched
Trigger matched
Timer fired
Condition passed / failed
Action started
Action completed
Command emitted
Automation faulted
Automation cancelled
```

Each entry should contain:

```text
AutomationId
AutomationName
AutomationType
InvocationId
```

## 33. Why-Did-It-Do-That Trace

For an automated command such as:

```text
kill guard
```

NexMUD should be able to show:

```text
Command: kill guard
Source: Automation
Automation: Attack alias
Cause: alias "kk guard"
Action: SendCommand[0]
Invocation: ...
```

This provenance model later extends to Mapper and Jev.

# Part XII: Persistence and Cache

## 34. Definition Persistence

Persist Automation as domain data:

```text
definition
schema version
enabled state
metadata
```

Do not persist generated JavaScript as the source of truth.

## 35. Compiled Artifact Cache

Cache key should include:

```text
Automation definition hash
Automation compiler version
Script API version
JavaScript target
```

Compiled artifacts are disposable and regenerable.

# Part XIII: Replay and Testing

## 36. Replay Requirements

Automation must work against recorded semantic events and controlled time.

Examples:

```text
replay output line
    -> text trigger matches
    -> command generated
```

```text
replay vitals event
    -> condition evaluates
    -> command generated
```

```text
advance virtual clock 30s
    -> recurring timer fires
```

## 37. Compiler Tests

Verify:

- valid IR compiles;
- invalid IR is rejected;
- permissions are derived correctly;
- code generation is deterministic;
- source/IR mappings are correct;
- generated code uses only SDK APIs;
- no raw CLR names appear;
- no private Jint bridge calls are emitted by the compiler.

## 38. Runtime Tests

Verify:

- enable;
- disable;
- reload;
- alias match;
- trigger match;
- timer fire;
- condition false;
- condition true;
- ordered actions;
- delay;
- storage read/write;
- cancellation during delay;
- cancellation during command;
- fault isolation.

## 39. Legacy Parity Tests

For each migrated feature:

```text
legacy behavior == scripting-runtime behavior
```

where identical behavior is intended.

Legacy-specific parity tests may be removed after the legacy executor is deleted, while behavior tests remain.

# Part XIV: UI Impact

## 40. Existing Automation UI

The existing Automation UI should continue editing typed Automation definitions.

It does not edit generated JavaScript.

Potential future advanced views may expose:

```text
Definition
Generated code
Runtime status
Diagnostics
```

but they are not required for this slice.

## 41. Runtime Status

Useful per-Automation state includes:

```text
Enabled
Running
Faulted
Last triggered
Last command
Last error
```

Do not build a full script debugger yet.

# Part XV: Legacy Removal

## 42. Removal Criteria

The legacy Automation executor may be removed only when:

- supported Alias behavior is migrated;
- supported Trigger behavior is migrated;
- supported Timer behavior is migrated;
- conditions are migrated;
- actions are migrated;
- enable/disable semantics match;
- persistence is migrated;
- replay tests pass;
- command provenance is correct;
- cancellation is deterministic;
- no production Automation definition requires the legacy path.

## 43. Final Ownership Boundary

After migration, Automation still owns:

```text
definitions
validation
IR
compiler
UI/editor
persistence
matching semantics where domain-specific
```

The scripting platform owns:

```text
execution
timers
task lifecycle
cancellation
host operations
storage execution
diagnostics plumbing
command dispatch integration
```

# Part XVI: Acceptance Criteria

## 44. SDK Acceptance

SDK stabilization is complete when:

- `nex.events` is stable for Automation use;
- `nex.commands` is stable for Automation use;
- `nex.state` exposes required read-only state;
- `nex.log` provides structured diagnostics;
- `nex.timers.delay/after/every/cancel` work through NexMUD scheduling;
- `nex.storage` provides isolated persistent JSON state;
- all APIs enforce capabilities in C#;
- APIs work in replay mode;
- no direct CLR exposure exists.

## 45. Automation Migration Acceptance

This slice is complete when:

- Automation definitions compile through an explicit IR;
- generated JavaScript runs through Jint;
- aliases execute through the scripting platform;
- triggers execute through the scripting platform;
- timers execute through `nex.timers`;
- conditions compile deterministically;
- actions use SDK APIs;
- Automation command provenance is preserved;
- enable/disable cancels all owned runtime work;
- hot update uses replacement semantics;
- Automation storage is namespaced;
- runtime faults do not affect unrelated Automation;
- replay tests pass;
- no migrated Automation feature uses direct Transport calls;
- legacy execution code has been removed for all migrated feature categories.

# Part XVII: Explicit Stop Point

After Automation migration, stop before adding large new user-facing scripting features.

The next architectural task should be:

```text
NexMUD Mapper Orchestration Architecture
```

That document should define `nex.mapper`, path-query primitives, movement-result semantics, route ownership, pause/resume/abort, replanning, recovery, command arbitration, and interaction with Automation and Jev.

Do not implement mapper orchestration through ad hoc Automation actions before that boundary is designed.

# Part XVIII: Roadmap

```text
Jint Integration
      |
      v
Scripting Vertical Slice
      |
      v
THIS SLICE:
SDK Stabilization
+ Timers
+ Storage
+ Automation IR
+ Automation Compiler
+ Automation Runtime Migration
      |
      v
Mapper Orchestration
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

# 46. Final Architectural Rule

Automation should remain easy for players to configure without requiring them to write code.

Internally, however:

> Every Automation behavior should execute through the same runtime primitives available to the rest of NexMUD.

That gives NexMUD one model for events, timers, state, commands, persistence, cancellation, diagnostics, replay, and future extensibility.

The user sees Automation.

The architecture sees one behavioral runtime.
