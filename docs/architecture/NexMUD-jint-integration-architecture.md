# NexMUD Jint Integration Architecture

**Status:** Adopted implementation architecture  
**Date:** 2026-09-26  
**Depends on:** NexMUD language-neutral scripting platform architecture

## Decision

NexMUD uses TypeScript as the supported authoring language and Jint as the initial in-process JavaScript execution engine:

```text
TypeScript source -> IScriptCompiler -> ES modules -> Jint -> NexMUD Script Host -> typed C# services
```

Jint is an adapter behind `JevMud.Scripting` contracts. It is not a dependency of Automation, Mapper, Jev, Codex, Core, Protocols, or Transport. Internal `JevMud.*` project/namespace names are retained during the product rename to avoid an unrelated binary/source migration.

## Project boundaries

```text
JevMud.Scripting
  language-neutral contracts, lifecycle, manifests, permissions,
  events, storage, compiler and diagnostics contracts

JevMud.Scripting.Jint
  Jint package, hardened engine construction, engine-per-script lifecycle,
  mailbox, private host bridge, module loader, diagnostics, JS bootstrap

JevMud.Scripting.TypeScript
  TypeScript authoring boundary, API declarations, compiler cache,
  future compiler adapter/source maps
```

Only `JevMud.Scripting.Jint` may reference the Jint NuGet package. `JevMud.Scripting` must remain free of runtime-engine dependencies.

## Runtime isolation and concurrency

Each independently managed script package owns one Jint `Engine`. Engines are never shared across unrelated user scripts. All entry into an engine occurs through a bounded, single-consumer dispatcher. Host continuations enqueue work; they never re-enter Jint directly from arbitrary continuation threads.

Each instance owns its manifest, resolved capability set, engine, mailbox, execution scope, event subscriptions, timers, pending host requests, storage namespace, diagnostics identity, and lifecycle state.

## Private host boundary

JavaScript receives narrow host delegates only. The bootstrap constructs an implementation-detail bridge around operations conceptually equivalent to:

```text
__nexHost.query(operation, payload)
__nexHost.begin(operation, requestId, payload)
__nexHost.cancel(requestId)
```

The public API is `nex` and the module specifier `@nexmud/api`. Scripts never receive repositories, SQLite connections, sockets, Avalonia controls, dependency-injection containers, or live domain objects.

Payloads are DTO-shaped strings, numbers, booleans, null, arrays, and plain objects. Capability checks occur in C# at the host boundary.

## Security posture

Production engines:

- never call `AllowClr`
- keep general CLR interop disabled
- disable `eval` / `new Function` string compilation
- disable CommonJS `require`
- use JevMUD/NexMUD-controlled in-memory modules only
- reject package-relative path traversal
- bound source/module sizes before registration
- apply Jint execution time, statement, memory, recursion, regex, array, JSON and cancellation constraints
- do not expose filesystem, network, process, reflection, raw database, or transport capabilities

Jint engine creation is centralized in `JintEngineFactory`.

## Modules

Allowed modules are built-in NexMUD modules, validated script-package modules, generated automation modules, and future generated Jev modules. There is no `node_modules`, npm, network import, arbitrary filesystem module discovery, or unrestricted `require`.

Compiled modules are registered explicitly through `Engine.Modules.Add`. `@nexmud/api` is provided as a built-in module.

## TypeScript boundary

Jint executes JavaScript only. TypeScript compilation lives behind `IScriptCompiler`. Compiler output consists of deterministic ES-module JavaScript, diagnostics, source identity, and source maps. Cache keys include source, compiler identity/options, and script API version.

The compiler implementation is intentionally independent from Jint. The initial integration establishes the boundary and versioned `@nexmud/api` declaration surface without introducing Node/Bun/Deno as an application dependency.

## Async host bridge

NexMUD owns asynchronous request semantics. JavaScript allocates deterministic request IDs and Promise resolvers. C# starts the host operation, returns immediately, then posts completion to the owning mailbox. The mailbox invokes `__nexResolve` or `__nexReject` on the serialized engine path.

Requests belong to the script instance and are cancelled on unload. Late completions after stop/reload are discarded.

## Events and backpressure

Core semantic events flow through the existing scripting event surface after Core state reduction. Script subscriptions enqueue immutable DTO snapshots into bounded channels and then into the owning Jint mailbox. Arbitrary script code never runs on the event producer thread.

Lossless lifecycle/control events must not silently disappear. Coalescing/high-volume policies may be added per event class, but queues remain bounded and overflow must be observable.

## Timers and replay

Public timers are owned by NexMUD's `IScriptScheduler`, not by hidden engine timers. Timer callbacks re-enter through the script mailbox. Owner shutdown cancels outstanding timers.

The broader runtime uses the NexMUD clock abstraction for orchestration and replay. The Jint adapter installs a custom time system backed by the same `IScriptScheduler` clock, so JavaScript `Date.now()` / `new Date()` observe controlled runtime time rather than an independent wall clock.

## Commands and provenance

Scripts never write to Transport. `nex.commands.send` routes through central command arbitration. The capability-bound host overwrites script-supplied provenance with the owning module identity and origin classification. Generated Automation, Jev, Mapper, and user Script execution retain their respective provenance categories.

## Storage

Jint receives no database handle. Script state uses the namespaced `IScriptStorage` contract. JavaScript globals are ephemeral and do not survive engine replacement.

## Lifecycle and reload

Lifecycle is:

```text
Created -> Compiled -> Loading -> Running -> Stopping -> Stopped -> Disposed
                         |            |
                         +-> Faulted -+
```

Hot reload is replacement: compile and validate a new version, create a new engine, initialize it, atomically swap, cancel the old instance, and dispose the old engine. Failed replacement leaves the existing instance active.

## Diagnostics

Runtime diagnostics carry script/module version, instance identity, invocation/operation/event correlation, timing/result, and source locations when available. Host-facing exceptions are sanitized before crossing into untrusted JavaScript. Source-map translation belongs to the compiler/diagnostics boundary.

## Acceptance rule

The integration is complete only when Jint types do not leak outside the adapter, engines are independently isolated and serialized, CLR/filesystem/network/database access is closed by default, host calls are capability checked, async completions re-enter through mailboxes, unload is deterministic, commands preserve provenance, timers and storage use shared platform services, replay can use a controlled clock, and replacing Jint does not require changes to domain consumers.

> Jint executes JavaScript. NexMUD owns everything around it.
