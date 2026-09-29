# Scripting Platform Implementation Status

**Target release:** NexMUD v0.16.0-alpha.1
**Architecture contract:** `NexMUD-scripting-platform-architecture.md`

## Implemented in alpha.1

1. Language-neutral scripting contracts in `NexMud.Scripting`.
2. Capability-gated host API for events, commands, state, mapper, Codex, storage, timers, UI and logging.
3. Structured execution ownership/cancellation through `ScriptExecutionSupervisor` / `ScriptExecutionScope`, including centralized owned-task fault reporting.
4. Ordered bounded event subscriptions through `ScriptEventHub`, with stable script event names, explicit backpressure instead of silent drops, automatic subscription teardown when an owner stops, and a state-reduction barrier so handlers cannot observe an event before Core has reduced it.
5. Provenance-aware integration with the controlled command/action pipeline. User, automation, Jev, mapper, and script commands share the same arbitration adapter before reaching `ActionProcessor`; capability hosts bind origin/module identity so authored scripts cannot spoof first-party provenance.
6. Namespaced script persistence in an independent script-state database.
7. Managed bootstrap runtime for first-party policy modules without selecting a public scripting language.
8. First compiled automation behavior: recurring command timers, with Automation-workspace activity surfaced through the runtime log host.
9. Mapper route execution migrated to shared command/scheduler/ownership primitives while graph/pathfinding remain typed services.
10. Jev execution mechanics migrated to shared command/scheduler/ownership primitives while decision policy remains typed.

## Architectural boundaries

The scripting project is dependency-free and cannot access transport, Client services, mapper/Codex SQLite schemas, GUI objects, filesystem/network/process APIs, or secrets. Client-side adapters translate typed services to stable host contracts.

`ActionProcessor` remains the authoritative transport-facing command-dispatch boundary. All application command producers now submit through `IScriptCommands`; the Client adapter centralizes human-override arbitration, automation command-rate limiting, ownership/provenance, and conversion into typed `MudActionEnvelope` values. Capability-gated hosts overwrite caller-supplied origin/owner/module fields with their bound module identity before dispatch.

Mapper graph, room identity, topology, pathfinding, persistence, and route calculation remain typed C# domain services. Only execution/orchestration is moving upward onto shared runtime primitives.

## Deferred intentionally

- Concrete JavaScript/Lua runtime selection.
- User-authored script loading and management UI.
- Full trigger/state-rule/workflow compiler migration.
- Full navigation policy module replacement for `AutoMoveService`.
- Plugin packaging/install model and privileged capability APIs.

These remain deferred until the shared contracts have been exercised by first-party behavior and local runtime tests. The managed bootstrap runtime is an architectural proving adapter, not the public scripting-language commitment.
