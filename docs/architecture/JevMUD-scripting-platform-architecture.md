# JevMUD Scripting Platform Architecture

**Status:** Proposed architecture  
**Baseline:** JevMUD v0.15.0-alpha.23  
**Date:** 2026-09-26

## 1. Architectural Direction

JevMUD should introduce a language-neutral scripting platform as a foundational execution layer for automation, Jev behavior, mapper behavior, and future extensibility.

Today, individual features tend to own both their domain concepts and their execution machinery. They react to events, inspect state, schedule work, maintain cancellation behavior, and issue commands independently.

The proposed architecture separates those responsibilities.

Features retain ownership of their domain models and algorithms. A shared scripting runtime owns programmable behavior and orchestration.

The intended flow is:

```text
Transport / Protocols
        |
        v
 Semantic Events
        |
        v
 Core State / Event Journal
        |
        +-----------------------------+
        |                             |
        v                             v
 Domain Services                Script Host API
 Mapper / Codex / etc.                |
                                      v
                              Scripting Runtime
                                      |
                    +-----------------+------------------+
                    |                 |                  |
                    v                 v                  v
               Automation           Jev             User Scripts
```

The scripting platform is not intended to replace strongly typed domain services. It provides a common execution model through which behavior can consume those services.

## 2. Core Principle

Infrastructure remains implemented in C#.

Behavior and policy may be expressed through scripts.

The distinction is important:

- Protocol parsing remains typed infrastructure.
- Core state remains typed infrastructure.
- Mapper graph storage and pathfinding remain typed domain services.
- Codex indexing remains a typed domain service.
- Command dispatch remains controlled infrastructure.
- Scheduling, event reactions, conditional behavior, navigation policy, aliases, triggers, and user-defined behavior can execute through the scripting platform.

Scripts must not become an alternative path around the architecture.

They operate through an explicit host API.

## 3. Language-Neutral Runtime

The architecture should not initially commit JevMUD to Lua, JavaScript, TypeScript, Python, or another language.

Define the runtime contracts first.

Conceptually:

```text
IScriptRuntime
IScriptContext
IScriptModule
IScriptHost
IScriptScheduler
IScriptEventSubscription
IScriptStorage
IScriptPermissionSet
```

A language implementation then adapts those contracts:

```text
IScriptRuntime
    |
    +-- JavaScriptRuntime
    +-- LuaRuntime
    +-- FutureRuntime
```

This allows the language decision to be evaluated independently from the JevMUD architecture.

JavaScript should be evaluated seriously because it minimizes authoring friction for maintainers familiar with JavaScript/TypeScript. Lua remains a reasonable embedded-runtime candidate, but familiarity with traditional MUD scripting should not determine the architecture.

TypeScript may eventually be supported as an authoring format compiled/transpiled to JavaScript rather than requiring a TypeScript runtime.

## 4. Script Host API

Scripts should receive capabilities from JevMUD rather than direct references to application internals.

A conceptual host API might expose:

```text
jev.events
jev.commands
jev.state
jev.mapper
jev.codex
jev.storage
jev.timers
jev.ui
jev.log
```

For example:

```text
events.on(...)
commands.send(...)
state.character(...)
state.room(...)
mapper.findPath(...)
mapper.moveTo(...)
codex.search(...)
storage.get(...)
storage.set(...)
timers.after(...)
timers.every(...)
log.info(...)
```

These APIs should be asynchronous where interaction with the event-driven core requires it.

The host API becomes a stable extensibility boundary.

## 5. Events

The existing event-driven architecture becomes especially valuable with scripting.

Scripts should subscribe to semantic JevMUD events rather than scrape arbitrary UI output whenever a semantic event exists.

Examples include:

```text
RoomEntered
RoomUpdated
CharacterVitalsChanged
CombatStarted
CombatEnded
MobObserved
MobConsidered
ItemObserved
ItemAcquired
QuestObserved
QuestCompleted
PromptReceived
CommandSent
ConnectionStateChanged
MapperRouteStarted
MapperRouteStep
MapperRouteBlocked
MapperRouteCompleted
MapperRouteAborted
```

Raw text events should remain available where necessary for user-defined triggers and unsupported game semantics.

The preferred hierarchy is:

```text
Protocol / text input
        -> parsing
        -> semantic event
        -> state reduction
        -> script notification
```

This prevents every automation from independently parsing the same game output.

## 6. Automation

Automation changes substantially.

Automation should no longer evolve into an independent execution engine.

The user-facing automation system remains valuable because most players should not need to write scripts for ordinary tasks.

Triggers, aliases, timers, conditions, and actions become a declarative layer over the scripting platform.

Conceptually:

```text
Automation Definition
        |
        v
 Automation Compiler
        |
        v
 Script Runtime Registration
```

A UI trigger such as:

```text
When:
    CharacterVitalsChanged
If:
    health < 25%
Then:
    send "flee"
```

is translated into runtime behavior using the same APIs available to authored scripts.

This gives JevMUD one implementation of:

- event subscriptions
- conditions
- timers
- cancellation
- command execution
- lifecycle management
- error handling
- logging
- persistence

instead of duplicating those capabilities inside automation.

## 7. Jev

Jev should become primarily a decision and policy layer rather than an execution subsystem.

Jev determines what should happen.

The scripting platform determines how behavior is orchestrated through the application.

For example, Jev may decide:

```text
Current target is unsafe.
Retreat.
Recover.
Return to previous objective when safe.
```

Execution can use the same mapper, command, scheduling, and state APIs exposed through the script host.

This also creates an important lifecycle for intelligent behavior:

```text
Jev discovers behavior
        |
        v
Behavior proves deterministic
        |
        v
Behavior becomes explicit automation/script
```

Jev does not need to repeatedly reason about behavior that has become deterministic.

This reduces cost, latency, unpredictability, and duplicated logic.

The global Jev enable/disable control remains authoritative. Disabling Jev must cancel or prevent Jev-owned runtime activity without disabling unrelated user automation.

## 8. Mapper

The mapper should not be converted into scripts wholesale.

Typed C# domain services should continue to own:

- room graph
- exits
- room identity
- topology
- pathfinding algorithms
- persistence
- route calculation
- map metadata

The execution policy surrounding navigation becomes scriptable.

For example:

```text
path = mapper.findPath(destination)

for each step:
    verify movement is currently safe
    issue movement command
    await RoomEntered or failure
    reconcile expected vs actual room
    recover, pause, replan, or abort
```

This prevents route execution from becoming an increasingly large mapper-specific state machine.

The mapper provides primitives. Runtime behavior composes them.

Autopilot therefore becomes a consumer of mapper services rather than part of the graph implementation itself.

## 9. Codex

Codex remains a typed domain subsystem responsible for discovered game knowledge.

The scripting host exposes a controlled query API.

Examples:

```text
codex.findMob(...)
codex.findItem(...)
codex.findRoom(...)
codex.search(...)
```

This allows automation, Jev, mapper policies, and user scripts to consume the same knowledge without coupling themselves to Codex persistence internals.

Scripts should not query Codex SQLite tables directly.

## 10. Persistence and SQLite

Raw SQLite access should not be the default scripting interface.

Direct database access would couple scripts to schema details and make migrations dangerous.

Persistence should instead be divided into two categories.

### Domain persistence

Owned exclusively by domain services:

- mapper data
- Codex data
- character state
- configuration
- other application-owned schemas

Scripts access these through domain APIs.

### Script persistence

Scripts receive namespaced key/value or document-oriented storage:

```text
storage.get(key)
storage.set(key, value)
storage.delete(key)
storage.list(prefix)
```

Storage should be scoped by script/module identity.

An advanced database API could be considered later, but it should be explicit, permissioned, and separate from normal script storage.

## 11. Scheduling

Timers and delayed execution should become a runtime primitive.

The scripting platform should own:

- one-shot timers
- recurring timers
- cancellation
- lifecycle binding
- disconnect behavior
- character/session ownership
- pause/resume semantics where appropriate

This avoids each subsystem implementing its own timer machinery.

A timer should have an owner.

Examples:

```text
UserScript
AutomationRule
JevSession
MapperRoute
Plugin
```

Stopping the owner cancels its outstanding work.

## 12. Cancellation and Structured Execution

Cancellation should be a first-class architectural concept.

Long-running behavior must have a defined lifetime.

Examples include:

- mapper navigation
- combat automation
- recovery
- quest execution
- scheduled triggers
- Jev plans

Runtime tasks should form an ownership hierarchy so stopping a route, disabling Jev, disconnecting, or unloading a script deterministically terminates associated work.

This is preferable to independent background tasks and ad hoc cancellation tokens distributed throughout features.

## 13. Permissions and Sandboxing

The scripting API must be capability-based.

A script should receive only the APIs it is allowed to use.

Potential capabilities include:

```text
ReadState
SendCommands
ReadMapper
ModifyMapper
ReadCodex
WriteScriptStorage
CreateTimers
SubscribeEvents
EmitUiNotifications
NetworkAccess
FileAccess
AdvancedDatabaseAccess
```

User scripts should not automatically receive:

- arbitrary filesystem access
- arbitrary network access
- application database access
- process execution
- unrestricted reflection
- access to secrets

This becomes increasingly important when scripts can eventually be shared or installed.

## 14. Command Arbitration

A common runtime also creates an opportunity to solve command contention centrally.

Jev, mapper navigation, automation, and user scripts may all want to issue commands.

They should not independently write to the transport.

All commands should continue through a controlled command-dispatch layer capable of supporting:

- ownership
- cancellation
- ordering
- throttling
- command provenance
- logging
- conflict policy

This makes it possible to answer:

```text
Why did JevMUD send this command?
```

The console can identify the source:

```text
USER
AUTOMATION
JEV
MAPPER
SCRIPT
SYSTEM
```

This aligns with the existing requirement for visibility into Jev's decisions and execution state.

## 15. Replay and Determinism

The scripting platform should integrate with the event journal.

Recorded semantic events can then be replayed through automation and scripts for testing.

This enables deterministic tests such as:

```text
Given this combat transcript
When these semantic events are replayed
Then this automation sends "flee"
```

or:

```text
Given this route
When movement fails at room 14
Then navigation replans rather than repeatedly sending "stand"
```

The scripting runtime should therefore avoid hidden wall-clock dependencies. Time should be supplied through an injectable clock abstraction where practical.

## 16. UI Impact

The existing UI concepts remain, but their implementation becomes more coherent.

### Automation UI

The current automation UI becomes a visual authoring layer over runtime behavior.

Advanced users may switch to or inspect script representation later.

### Jev console

The Jev console can display:

- decision
- selected action
- script/automation invoked
- command provenance
- active tasks
- cancellation
- mapper route state
- errors

### Script management

Future UI can provide:

- installed scripts
- enable/disable
- permissions
- logs
- errors
- configuration
- editor integration

A built-in full IDE is not required initially.

## 17. Extensibility

This architecture prepares JevMUD for plugins without requiring a plugin system immediately.

The script host API naturally becomes one extensibility surface.

A future plugin model could include:

```text
Declarative Automation
        |
Scripts
        |
Managed Plugins
        |
Core JevMUD
```

Each level receives progressively more capability.

This is substantially safer than allowing extensions to reach directly into application internals.

## 18. What Does Not Move Into Scripts

The introduction of scripting should not turn JevMUD into a dynamically typed application.

The following should remain strongly typed C# concerns:

- transport
- Telnet negotiation
- MCCP
- GMCP
- ANSI processing
- semantic parsing infrastructure
- event bus
- core state model
- mapper graph
- pathfinding algorithms
- persistence repositories
- security boundaries
- script host
- command arbitration
- lifecycle management

Scripts orchestrate these capabilities.

They do not replace them.

## 19. Resulting Architectural Shape

The resulting architecture becomes:

```text
+------------------------------------------------------+
|                       UI                             |
| Automation Editor | Mapper | Codex | Jev | Scripts  |
+------------------------------------------------------+
                         |
+------------------------------------------------------+
|              Behavior / Policy Layer                 |
| Automation | Jev | Navigation Policy | User Scripts |
+------------------------------------------------------+
                         |
+------------------------------------------------------+
|                 Scripting Platform                   |
| Runtime | Events | Scheduler | Storage | Permissions |
+------------------------------------------------------+
                         |
+------------------------------------------------------+
|                    Host API                          |
| Commands | State | Mapper | Codex | UI | Logging    |
+------------------------------------------------------+
                         |
+------------------------------------------------------+
|                  Domain Services                     |
| Mapper | Codex | Character | Combat | Quest | etc.  |
+------------------------------------------------------+
                         |
+------------------------------------------------------+
|             Semantic State / Event Core              |
+------------------------------------------------------+
                         |
+------------------------------------------------------+
|               Protocol / Transport                   |
| Telnet | MCCP | GMCP | ANSI | Avendar Adapter       |
+------------------------------------------------------+
```

The critical dependency direction is downward.

Scripts depend on stable host contracts. Domain services do not depend on individual scripts.

## 20. Architectural Consequences

The primary benefit is not merely that JevMUD gains scripting.

The important change is that JevMUD gains one programmable execution model.

Without this layer, continued development risks creating separate execution machinery for:

- automation
- mapper autopilot
- combat behavior
- quest behavior
- Jev
- aliases
- triggers
- timers
- future plugins

Those systems would eventually need to solve the same problems independently: scheduling, event handling, cancellation, persistence, diagnostics, command arbitration, and lifecycle management.

Introducing the scripting platform before those subsystems become deeply entrenched avoids that duplication.

The design principle is therefore:

> Domain features own knowledge and algorithms. The scripting platform owns programmable orchestration.

That boundary should guide subsequent automation, mapping, Jev, and extensibility work.

## 21. Recommended Implementation Sequence

Before expanding the current automation and mapper execution systems further:

1. Define the language-neutral scripting contracts.
2. Define the host API and capability model.
3. Define runtime task ownership and cancellation semantics.
4. Define event subscription semantics.
5. Define command provenance/arbitration integration.
6. Define namespaced script persistence.
7. Implement a minimal runtime adapter using the selected initial language.
8. Port one existing automation behavior onto the runtime.
9. Port mapper route execution onto the shared orchestration primitives where appropriate.
10. Integrate Jev execution with the same host APIs.
11. Only then expand the user-facing scripting and plugin/extensibility surfaces.

This sequence tests the architecture against real JevMUD behavior before committing to a large public scripting API.