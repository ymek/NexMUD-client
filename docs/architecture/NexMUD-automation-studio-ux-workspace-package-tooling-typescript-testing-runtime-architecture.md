# NexMUD Automation Studio UX, Workspace, Package Tooling, TypeScript Authoring, Testing & Runtime Architecture

**Status:** Proposed implementation architecture / implementation directive  
**Date:** 2026-10-01  
**Product:** NexMUD  
**Repository:** `ymek/NexMUD-client`  
**Current application version observed:** `0.31.0`  
**Primary platform:** macOS; architecture must remain portable to Windows and Linux  
**Scope:** Automation Studio UX, automation organization, structured workflow authoring, real filesystem script workspaces, npm-compatible package management, bundled self-contained Node/pnpm tooling, TypeScript language services, Monaco integration, first-class testing, build/deployment/runtime integration, diagnostics, profile switching, migration, and incremental delivery.  
**Depends on:**
- NexMUD Scripting Platform Architecture
- NexMUD Jint Integration Architecture
- NexMUD Scripting Vertical Slice Architecture
- NexMUD Scripting SDK and Automation Migration Architecture
- NexMUD Input, Keybinding, and Output Transformation Architecture

**Jev constraint:** Jev remains intentionally out of scope. Preserve architectural seams for future Jev consumption, but do not migrate, redesign, or extend Jev as part of this work.

---

# 1. Decision

NexMUD Automation Studio will be redesigned as a purpose-built automation workbench with three distinct authoring modes sharing one application shell:

1. **Automations** for fast structured authoring of aliases, triggers, semantic triggers, keybindings, timers, state rules, highlights, and similar declarative behavior.
2. **Workflows** for structured multi-step composition, initially as a vertical step editor rather than a graph canvas.
3. **Scripts** for real TypeScript package development using a conventional filesystem, standard `package.json`, npm-registry dependencies, pnpm-compatible tooling, Monaco, and a real TypeScript language service.

The current experience must not be incrementally decorated into permanence. The workbench architecture must be corrected first.

Monaco remains an embedded editor component. Monaco is **not** the workspace, filesystem, package manager, language server, build system, test runner, runtime, or product architecture.

The script authoring model will use standard npm ecosystem conventions where they are useful while preserving Jint as the runtime:

```text
TypeScript source + npm dependencies
              |
              v
       NexMUD toolchain
       - pnpm restore
       - TypeScript analysis
       - compatibility validation
       - controlled bundling
       - tests
              |
              v
    validated JS artifact
              |
              v
       isolated Jint runtime
              |
              v
        NexMUD Script SDK
```

**Node is an authoring/build tool, not the game scripting runtime.**

NexMUD must ship self-contained. A user must be able to drag NexMUD from the `.dmg` into Applications and immediately author, install, build, test, and run scripts without installing Node, npm, pnpm, TypeScript, an editor extension, or any separate SDK.

---

# 2. Why This Redesign Is Required

The current Studio implementation has useful underlying pieces, but the product model is wrong in several important ways.

Current repository observations:

- `src/NexMud.Gui/AutomationStudio/AutomationStudioWindow.cs` implements a fixed Explorer / document / Details layout with a permanently allocated bottom panel.
- `src/NexMud.Gui/AutomationStudio/StudioModel.cs` treats Automation definitions and script files as variants of one generic `StudioDocumentKind` model and identifies Automation documents by array index.
- `src/NexMud.Gui/AutomationStudio/AutomationDocumentEditor.cs` already contains useful structured Automation editors; these should be preserved and improved rather than replaced with raw code.
- `src/NexMud.Gui/MonacoEditorHost.cs` provides a narrow local WebView bridge, but it is an editor bridge rather than an IDE/workspace architecture.
- `src/NexMud.Gui/Assets/Monaco/nexmud-editor-bridge.js` creates Monaco models and registers the NexMUD SDK declarations, but it does not provide a real project-aware filesystem-backed TypeScript language service.
- The current Monaco bridge uses only browser-side Monaco TypeScript workers and injected `@nexmud/api` declarations. It has no project-aware `node_modules`, real `tsconfig` project graph, package dependency resolution, filesystem change awareness, or external TypeScript language server.
- `src/NexMud.Client/Scripting/ScriptWorkspaceService.cs` already stores script sources as real files under profile-scoped directories and has basic create/rename/delete/build operations. This is a good seam to evolve.
- The current script package manifest is proprietary `manifest.json`.
- The current `TypeScriptCompiler` invokes an external `tsc` executable found on the user's environment. This violates the self-contained product requirement.
- The current compiler emits JavaScript modules but does not bundle npm dependencies.
- `JintModuleLoader` currently permits only package-relative imports plus `@nexmud/api`, so bare npm package imports cannot execute.
- The scripting runtime already has valuable properties which must be retained: capability checks, isolated Jint engines, resource limits, serialized execution, source maps, last-known-good reload behavior, and command provenance.
- `AutomationIr.cs` already defines structured triggers, conditions, and actions. This should become the primary UI vocabulary rather than exposing generated JavaScript.

The redesign therefore must preserve the good domain/runtime work while replacing the weak workbench and tooling assumptions.

---

# 3. Architectural Invariants

These rules are mandatory throughout implementation.

## 3.1 Automation remains declarative

Automation owns user intent. It must not become a disguised code editor.

Aliases, triggers, semantic triggers, keybindings, timers, state rules, highlights, and workflows are edited through structured controls by default.

Generated JavaScript is implementation output and is not the normal authoring format.

## 3.2 Code is shown when code actually exists

If an Automation action invokes a script function, the UI must show the package/function reference and provide `Open Definition`.

If a user is authoring TypeScript, the Scripts activity provides the code experience.

Do not manufacture a TypeScript document for a simple alias such as `cake -> eat cake`.

## 3.3 Scripts are real files

Script packages use ordinary directories and source files on disk.

Source must not be trapped in SQLite, serialized into a settings blob, or owned exclusively by Monaco.

Users must be able to reveal package directories in Finder and use external tools if they choose.

## 3.4 Standard package metadata

New script packages use `package.json`, not a new NexMUD-only package manifest.

NexMUD-specific metadata lives under a namespaced `nexmud` property.

## 3.5 npm-compatible ecosystem, controlled execution

NexMUD may resolve packages from the npm registry and use standard package metadata.

NexMUD must not automatically execute arbitrary npm lifecycle scripts.

NexMUD initially exposes controlled operations only:

- Install / Restore
- Update
- Build
- Test
- Clean

Arbitrary `package.json` scripts are not surfaced as runnable Studio commands in the initial implementation.

## 3.6 Jint remains the runtime

Runtime execution continues through Jint and the existing NexMUD capability host.

Node, pnpm, TypeScript, esbuild, test tooling, and language servers are authoring/build-time infrastructure only.

## 3.7 One profile equals one Studio context

Changing the Profile selector replaces the entire Studio context:

- open Automation documents;
- open script files;
- active activity;
- file explorer state;
- package list;
- language-server workspace;
- search scope;
- problems;
- test results;
- runtime diagnostics displayed by the Studio.

No tabs or Monaco models from the previous profile may remain attached to the new context.

## 3.8 Runtime behavior remains last-known-good

A failed source edit, dependency restore, build, or test must not replace a currently running good script artifact.

Hot replacement occurs only after a successful compatible build.

## 3.9 Studio operations must not block Avalonia

File scanning, package restore, TypeScript analysis, builds, tests, searches, and Jint validation must be asynchronous and cancellable.

No process wait, filesystem walk, or compiler invocation may synchronously block the UI thread.

## 3.10 Jev remains untouched

Do not add `nex.jev`, move Jev into the Studio, or alter Jev execution in this implementation.

---

# 4. Product Principles

The Studio must optimize for the following priorities, in this order:

1. Easy to use.
2. Easy to understand.
3. Easy to navigate.
4. Fast to configure for common MUD-client automation.
5. Powerful enough for experienced users.
6. Familiar to developers when code is involved.
7. Transparent about runtime state and failures.

The product should borrow proven IDE interaction conventions without becoming a general-purpose embedded VS Code clone.

The Studio should feel like a NexMUD automation product which happens to contain a capable TypeScript IDE.

---

# 5. Non-Goals

This work does not include:

- Jev migration or redesign;
- a VS Code extension host;
- a general extension marketplace;
- Git/source-control UI;
- Docker/container integrations;
- SSH/remote workspaces;
- arbitrary terminal access;
- running arbitrary `package.json` scripts;
- npm lifecycle execution;
- Node.js as the NexMUD script runtime;
- exposing raw CLR objects to scripts;
- exposing arbitrary filesystem access to Jint scripts;
- graph-based workflow editing in the first implementation;
- multi-profile simultaneous Studio workspaces;
- cloud script synchronization;
- package publishing to npm from Studio;
- downloading a compiler/toolchain on first launch.

---

# 6. Information Architecture

The Studio shell has five primary activities:

```text
Automation Studio
|
+-- Automations
|   +-- Aliases
|   +-- Triggers
|   +-- Semantic Triggers
|   +-- Keybindings
|   +-- Timers
|   +-- State Rules
|   +-- Highlights
|
+-- Workflows
|
+-- Scripts
|
+-- Search
|
+-- Runtime
```

These are workbench activities, not document types.

Each activity owns its own navigation model and authoring surface.

## 6.1 Activity rail

Use a narrow left activity rail similar in interaction concept to established IDEs, with NexMUD-specific activities only.

Required activities:

- Automations
- Scripts
- Workflows
- Search
- Runtime

The selected activity controls the secondary sidebar/explorer.

Do not put every automation and every script file in one combined tree.

## 6.2 Contextual explorer

The explorer changes by activity.

### Automations explorer

```text
AUTOMATIONS

Aliases
  Combat
    flee
    assist
  Utility
    cake

Triggers
  Combat
  Looting

Semantic Triggers
Keybindings
Timers
State Rules
Highlights
```

### Scripts explorer

```text
SCRIPTS

combat-tools
  src
    main.ts
    combat.ts
  test
    combat.test.ts
  package.json
  tsconfig.json
  README.md

navigation
  ...
```

### Workflows explorer

```text
WORKFLOWS

Combat
  emergency-retreat
  assist-group
Navigation
  restock
```

### Search explorer

Shows query, filters, and results grouped by Automation / Workflow / Script package.

### Runtime explorer

Shows active Automation runtime generation, loaded script packages, status, invocation/runtime faults, and package enablement state.

---

# 7. Workbench Shell

Target structure:

```text
+--------------------------------------------------------------------------------+
| Automation Studio        Profile: Avendar v                    Runtime: Running |
+-----+----------------------+---------------------------------------------------+
|     |                      | editor / designer tabs                            |
|  A  | contextual explorer +---------------------------------------------------+
|     |                      |                                                   |
|  S  |                      |                                                   |
|     |                      |          authoring surface                         |
|  W  |                      |                                                   |
|     |                      |                                                   |
|  Q  |                      |                                                   |
|     |                      |                                                   |
|  R  |                      |                                                   |
|     |                      |                                                   |
+-----+----------------------+---------------------------------------------------+
| Problems  Output  Tests  Runtime  Events                                ^   2 |
+--------------------------------------------------------------------------------+
| status / current item / language / build state / runtime state                |
+--------------------------------------------------------------------------------+
```

## 7.1 Remove permanent Details sidebar

The current permanent Details/Inspector sidebar must not remain part of the default layout.

Primary edit controls belong inside the authoring surface.

If a future authoring mode genuinely benefits from a secondary property inspector, it must be toggleable and contextual, not a fixed third column in every activity.

## 7.2 Bottom panel defaults collapsed

The Problems / Output / Tests / Runtime / Events panel defaults to collapsed.

It opens when:

- the user explicitly opens it;
- a foreground Build/Test action completes with errors and the user preference permits auto-reveal;
- the user clicks a status/badge;
- an operation requires interactive review.

A stale single error must never reserve 25-30% of the Studio window.

## 7.3 No global toolbar soup

Remove the current broad row of unrelated commands such as Save, Save All, New Package, New TS, Build, Run, Search, Panel, Quick Open from one permanent toolbar.

Use:

- standard shortcuts;
- context-sensitive command buttons;
- explorer context menus;
- command palette;
- Quick Open;
- compact activity-specific actions.

The top shell should remain visually quiet.

## 7.4 Required global commands

The Studio command system must support at least:

```text
Save
Save All
Quick Open
Command Palette
Find
Search Workspace
Build Active Package
Build All Changed Packages
Test Active Package
Run Selected Script Export
Toggle Bottom Panel
Reveal in Finder
Restart TypeScript Language Service
Reload Studio Workspace
```

Exact keybindings should follow platform conventions and the existing NexMUD keybinding architecture.

---

# 8. Document Model

The current `StudioDocumentKind` approach conflates Automation objects and script files. Replace it with explicit document identities.

Recommended model:

```csharp
public abstract record StudioDocumentId(string ProfileId);

public sealed record AutomationDocumentId(
    string ProfileId,
    AutomationKind Kind,
    string AutomationId) : StudioDocumentId(ProfileId);

public sealed record WorkflowDocumentId(
    string ProfileId,
    string WorkflowId) : StudioDocumentId(ProfileId);

public sealed record ScriptFileDocumentId(
    string ProfileId,
    string PackageId,
    string CanonicalPath) : StudioDocumentId(ProfileId);
```

Do not use collection indices as document identity.

Deleting/reordering an alias must not change the identity of another open alias.

## 8.1 Stable Automation IDs

All persisted Automation definitions must have stable IDs.

Existing optional `Id` fields must be normalized during migration.

Highlights or other definitions which do not currently have a stable ID must gain one through either:

- a durable ID on the domain record; or
- a durable profile-scoped catalog wrapper.

Do not use display names as identity.

## 8.2 Tabs

Tabs represent open authoring documents.

Requirements:

- stable identity;
- dirty indicator;
- close button;
- middle-click close where supported;
- close others / close right context commands;
- restore focus to neighbor when closing;
- preserve cursor/selection per script file;
- preserve scroll position per structured document when feasible;
- tabs do not survive a profile switch unless restored later as profile-scoped session state.

---

# 9. Automations Activity

The Automations activity is optimized for users who do not need to write code.

The current structured `AutomationDocumentEditor` is a useful starting point, but it must become part of a clearer workbench rather than a generic document host.

## 9.1 Automation categories

Required first-class categories:

- Aliases
- Triggers
- Semantic Triggers
- Keybindings
- Timers
- State Rules
- Highlights

Workflows move to their own activity.

## 9.2 Automation folders

Each category supports user-created folders.

Example:

```text
Aliases
  Combat
    assist
    rescue
  Movement
    recall
  Utility
    cake
```

Folder operations:

- New Folder
- Rename Folder
- Delete Empty Folder
- Delete Folder and Contents with confirmation
- Move item by drag/drop
- Move item through context menu
- Move folder
- Duplicate item
- Search/filter

### Folder semantics

Folders are **authoring organization only**.

Do not conflate Studio folders with the existing Automation `Group` field.

`Group` is a runtime concept used by Automation enable/disable semantics. A folder is a navigational concept.

A trigger may be located in folder `Combat/Defensive` while its runtime Group remains `combat`.

## 9.3 Quick creation

Creating an Automation should require only the minimum useful information.

Examples:

### Alias

```text
Name / match: cake
Send command: eat cake
[Create]
```

### Trigger

```text
Pattern: You are hungry.
Send command: eat bread
[Create]
```

After creation, open the full editor for advanced configuration.

Do not force users through every advanced property before creating a simple automation.

## 9.4 Common structured editor anatomy

Each Automation editor should use a consistent visual grammar:

```text
Alias: cake                                          Enabled [on]

MATCH
  Alias
  cake

WHEN
  No conditions
  + Add condition

DO
  1. Send command
     eat cake

     + Add action

BEHAVIOR / ADVANCED
  Group
  Priority
  cooldown / stop-processing / one-shot where applicable
```

Use progressive disclosure. Common properties are visible. Advanced controls remain available without dominating the page.

## 9.5 Conditions

Conditions are structured and map directly to `AutomationCondition` types.

Required UI support for current IR types:

- State expression
- Event field comparison
- Regex
- Contains
- Storage value comparison
- Script predicate
- AND group
- OR group
- NOT

Nested conditions must be visually understandable. Avoid raw JSON.

## 9.6 Actions

Actions map directly to `AutomationAction` types.

Required UI support for current IR types:

- Send command
- Send commands
- Delay
- Log
- Set storage
- Delete storage
- Run script function

Action controls must support:

- add;
- remove;
- reorder;
- duplicate;
- inline validation;
- collapse/expand for long lists.

## 9.7 Script references

For `RunScriptFunctionAutomationAction` or `ScriptPredicateAutomationCondition`:

```text
Run script function
Package: combat-tools
Function: shouldFlee
Arguments: ...

[Open Definition]
```

`Open Definition` changes to Scripts activity, opens the source file containing the export, and navigates to the symbol.

Do not show generated Automation JavaScript.

## 9.8 Generated source

A read-only `View Generated Runtime Source` command may exist under developer/advanced diagnostics.

It must never be the normal Automation editing surface.

---

# 10. Workflows Activity

Workflows are higher-level structured automation, not source files and not initially node graphs.

## 10.1 Initial workflow editor

Use a vertical step editor:

```text
Workflow: Emergency Retreat                           Enabled [on]

TRIGGER
  Event: character.vitalsChanged
  Condition: hp.percent < 25

STEPS
  1. Send command
     flee

  2. Delay
     500 ms

  3. Run script function
     navigation / chooseSafeDirection

  4. Send command
     ${result}

  + Add step

BEHAVIOR
  Failure: Stop
  Cooldown: 1000 ms
  One shot: off
```

## 10.2 Initial step vocabulary

Use the existing Automation action vocabulary first.

Do not design a graph engine before the structured semantics are proven.

Later workflow versions may add explicit branches, loops, waits, parallelism, and event waits, but those must be separate architecture work.

## 10.3 Legacy workflow migration

Current `AutomationWorkflow.Steps` is a string DSL.

Migration must be lossless.

Introduce a `LegacyWorkflowStepAdapter` with this behavior:

1. Parse the legacy step string using the existing runtime semantics.
2. Convert losslessly representable operations into structured step models.
3. If any segment cannot be represented safely, keep the original legacy text and surface a migration warning.
4. Never silently rewrite an unparseable workflow.
5. Only persist a new structured representation after successful round-trip validation.

Do not delete support for legacy workflow text in the same change which introduces the new editor.

---

# 11. Scripts Activity UX

The Scripts activity should be immediately familiar to users of VS Code-style editors without attempting to recreate the entire VS Code product.

## 11.1 Explorer behavior

Each package is a root node.

Required filesystem operations:

- create file;
- create folder;
- rename file;
- rename folder;
- move file/folder;
- duplicate file;
- delete file/folder;
- reveal in Finder;
- copy relative path;
- refresh;
- collapse all;
- create package;
- rename package display name;
- delete package;
- package properties.

Use inline rename/create UX where practical rather than a modal for every operation.

## 11.2 File visibility

Show ordinary package content.

Hide generated/runtime plumbing by default:

- `node_modules`
- `.nexmud`
- build outputs
- temporary files

Provide a `Show Generated/Hidden Files` preference later if needed.

## 11.3 Editor tabs

Required script editor behavior:

- multiple open files;
- persistent tab ordering during the session;
- dirty markers;
- source language indicator;
- cursor location;
- breadcrumbs;
- go to definition;
- find references;
- rename symbol;
- hover docs;
- signature help;
- completion;
- diagnostics;
- document symbols;
- format document when formatter support is added;
- Quick Open;
- command palette.

## 11.4 Split editors

Split editors are desirable but not required in the first implementation slice.

Architect `EditorGroup` separately from `DocumentModel` so a second editor group can be added later without rewriting the model lifecycle.

## 11.5 Empty state

When Scripts contains no package:

```text
No script packages yet.

[Create Package]  [Import Package]

Scripts are TypeScript packages executed by NexMUD's isolated Jint runtime.
NexMUD includes all required tooling.
```

Do not show an empty Monaco surface before a file exists.

---

# 12. Package Model

New packages use standard `package.json`.

Recommended initial shape:

```json
{
  "name": "combat-tools",
  "version": "1.0.0",
  "private": true,
  "type": "module",
  "packageManager": "pnpm@<bundled-version>",
  "dependencies": {},
  "devDependencies": {},
  "nexmud": {
    "id": "pkg_01J...",
    "apiVersion": "1",
    "entry": "./src/main.ts",
    "permissions": [
      "state.read",
      "events.subscribe",
      "commands.send",
      "timers.create",
      "storage.read",
      "storage.write",
      "log.write"
    ]
  }
}
```

## 12.1 Package identity

`name` is normal npm-style package identity/display metadata.

`nexmud.id` is a stable NexMUD package identity used by Automation references and runtime state.

Renaming the package directory or changing `name` must not break Automation references.

Use a generated opaque stable ID for new packages. Do not infer identity from array position or filesystem path.

## 12.2 Entrypoint

Only exports reachable from the declared NexMUD entrypoint are public script exports.

Automation function pickers must not expose arbitrary internal functions from arbitrary files unless they are exported through the package public entrypoint.

This creates a clear package API boundary.

Example:

```ts
// src/main.ts
export { shouldFlee } from "./combat";
export { chooseSafeDirection } from "./navigation";
```

## 12.3 Permissions

`nexmud.permissions` maps to existing `ScriptCapability` values through a stable external string contract.

Do not serialize C# enum names as the long-term public package format.

Example mapping:

```text
state.read            -> ReadState
events.subscribe      -> SubscribeEvents
commands.send         -> SendCommands
timers.create         -> CreateTimers
storage.read          -> ReadScriptStorage
storage.write         -> WriteScriptStorage
log.write             -> Log
mapper.read            -> ReadMapper
mapper.pathfind        -> MapperPathfind
mapper.move            -> MapperMove
mapper.route.observe   -> MapperRouteObserve
codex.read             -> ReadCodex
```

Any future high-risk capabilities such as network or file access require explicit architecture/security review and must not be implicitly enabled by npm packages.

## 12.4 Runtime enabled state

Package enable/disable state is profile runtime state and must not be treated as package source metadata.

Store enablement separately from `package.json`.

Importing a package must default to disabled until the user explicitly enables it.

## 12.5 `package.json` scripts

Preserve a package's `scripts` field if present, but do not automatically execute it and do not initially expose generic `Run npm script` UI.

NexMUD-controlled Build/Test/Clean commands use NexMUD-owned tooling and generated configuration.

---

# 13. Profile Script Workspace Layout

Retain the existing profile-scoped script root to minimize migration risk:

```text
<NexMUD data>/
  profiles/
    <profileId>/
      scripts/
        pnpm-workspace.yaml
        pnpm-lock.yaml
        .npmrc
        .nexmud/
          workspace.json
          sdk/
          test-support/
          build/
          diagnostics/

        combat-tools/
          package.json
          tsconfig.json
          src/
            main.ts
            combat.ts
          test/
            combat.test.ts
          README.md

        navigation/
          package.json
          tsconfig.json
          src/
            main.ts
```

Existing packages currently located directly under `profiles/<profileId>/scripts/<packageId>` may remain direct children. Do not introduce an unnecessary `packages/` directory during migration.

## 13.1 Workspace files

`pnpm-workspace.yaml`, `.npmrc`, SDK projection files, and the common lockfile are NexMUD-managed.

Suggested workspace definition:

```yaml
packages:
  - "*"
```

Exclude `.nexmud` and any generated directories.

## 13.2 Lockfile strategy

Use one profile-scoped `pnpm-lock.yaml`.

Reasons:

- packages inside one Studio profile form one coherent authoring workspace;
- local `workspace:*` dependencies become possible;
- duplicate dependency storage is reduced;
- restore is deterministic for the profile;
- language tooling sees one consistent dependency graph.

A full profile export includes the lockfile.

A single-package export may omit the workspace lockfile and resolve against its declared semver ranges on import. Package-level reproducibility can be revisited if standalone package exchange becomes a major workflow.

## 13.3 pnpm store

Use a NexMUD-owned global pnpm content-addressed store under application data/cache rather than each package carrying duplicate physical copies.

Do not use the user's global pnpm store implicitly.

---

# 14. Automation Storage and Folder Metadata

Runtime Automation definitions should remain in their existing durable profile/settings persistence path during the initial Studio redesign.

Do not combine the Studio rewrite with an unnecessary runtime persistence rewrite.

Add a profile-scoped Automation organization store for authoring metadata:

```json
{
  "schemaVersion": 1,
  "folders": [
    {
      "id": "folder_...",
      "kind": "alias",
      "parentId": null,
      "name": "Combat",
      "order": 10
    }
  ],
  "items": [
    {
      "automationId": "alias_...",
      "folderId": "folder_...",
      "order": 20
    }
  ]
}
```

This metadata must not change Automation runtime behavior.

Folder deletion/moves must be transactional at the service boundary so navigation metadata cannot orphan Automation items.

---

# 15. Bundled Toolchain

NexMUD ships the entire authoring toolchain inside the application distribution.

## 15.1 Required toolchain components

Pin and package compatible versions of:

- Node.js LTS runtime;
- pnpm;
- TypeScript / `tsserver`;
- `typescript-language-server` or equivalent TypeScript LSP adapter;
- esbuild or equivalent deterministic bundler;
- Vitest or the selected controlled TypeScript test runner;
- Monaco web assets;
- Monaco language-client bridge dependencies where required.

Exact versions must be pinned in repository-controlled build metadata.

Do not depend on binaries from the user's `PATH`.

## 15.2 macOS bundle layout

Conceptual app layout:

```text
NexMUD.app/
  Contents/
    MacOS/
      NexMUD
    Resources/
      AutomationStudio/
        monaco/
        tooling/
          node/
          pnpm/
          typescript/
          typescript-language-server/
          esbuild/
          vitest/
        manifest.json
```

The exact packaging path may differ, but `ToolchainLocator` must resolve only app-owned binaries/resources by default.

## 15.3 No first-run downloads

Launching Scripts on a clean machine with no Node installed must work without downloading a compiler.

Dependency installs still require network access when the user's package declares registry dependencies, but the compiler/package manager itself must already be present.

## 15.4 Toolchain integrity

Ship a toolchain manifest containing:

- component name;
- pinned version;
- relative path;
- architecture;
- SHA-256 hash where practical.

On startup/use, `ToolchainHealthService` validates required executables lazily.

A broken scripting toolchain must not prevent the base MUD client from launching.

The Scripts activity should show a targeted tooling error instead.

## 15.5 Packaging scripts

Extend the existing macOS packaging/build scripts to copy, sign, and validate the toolchain.

The packaging acceptance test must run on an environment where system `node`, `npm`, `pnpm`, and `tsc` are unavailable.

---

# 16. Package Manager Architecture

Introduce an explicit package-management boundary.

Recommended interface:

```csharp
public interface IScriptPackageManager
{
    Task<PackageRestoreResult> RestoreAsync(ProfileId profileId, CancellationToken ct);
    Task<PackageUpdateResult> UpdateAsync(ProfileId profileId, PackageId? packageId, CancellationToken ct);
    Task<PackageInstallResult> InstallAsync(ProfileId profileId, CancellationToken ct);
    Task<PackageCleanResult> CleanAsync(ProfileId profileId, PackageId? packageId, CancellationToken ct);
}
```

Do not make `AutomationStudioWindow` spawn pnpm.

## 16.1 Process invocation

All tooling processes must be started without a shell.

Use explicit executable + argument arrays where supported.

Sanitize environment variables.

Set:

- controlled `PATH` containing only the bundled toolchain requirements;
- NexMUD-owned pnpm store path;
- workspace root as working directory;
- lifecycle scripts disabled;
- telemetry/update-notifier features disabled where applicable.

Capture stdout and stderr asynchronously.

Support cancellation by terminating the process tree.

## 16.2 Lifecycle scripts

All install/restore operations must use `--ignore-scripts` and equivalent config safeguards.

Do not rely on only one flag. The generated `.npmrc` should also state the policy.

The Studio must never run package `preinstall`, `install`, `postinstall`, `prepare`, or similar hooks automatically.

## 16.3 Dependency sources

Initial supported dependency sources:

- npm registry semver dependencies;
- profile-local `workspace:*` dependencies.

Initially reject or warn/block:

- git URLs;
- SSH dependencies;
- arbitrary HTTP tarball URLs;
- external `file:` paths;
- `link:` paths escaping the profile workspace.

This keeps package acquisition predictable and constrains filesystem/network behavior.

## 16.4 Lockfile behavior

`Restore`:

- use frozen lockfile when a lockfile exists;
- fail clearly if `package.json` changed and lockfile is stale;
- offer `Install/Update Lockfile` as the corrective action.

`Install/Update`:

- intentionally permits lockfile updates;
- writes lockfile atomically where possible;
- refreshes language service and package diagnostics after completion.

Build must never silently update the lockfile.

---

# 17. Package Compatibility Contract

NexMUD supports npm packages only when they can be converted into a runtime artifact compatible with Jint.

Package availability on npm does not imply NexMUD compatibility.

## 17.1 Supported initial class

Supported:

- pure JavaScript/TypeScript libraries;
- ESM or CommonJS libraries which esbuild can statically bundle into the target artifact;
- JSON data imported through the bundler;
- libraries whose runtime behavior depends only on ECMAScript features available in the configured Jint target;
- local NexMUD workspace packages.

## 17.2 Unsupported initial class

Reject with explicit diagnostics:

- Node built-ins such as `fs`, `net`, `tls`, `http`, `https`, `child_process`, `worker_threads`, `cluster`, or `module`;
- `node:` imports;
- native `.node` addons;
- packages requiring native install/build steps;
- unresolved dynamic `require`;
- runtime dynamic imports which produce additional untracked chunks;
- WebAssembly unless separately approved later;
- packages requiring browser globals such as `window` or `document` when those globals are not provided by NexMUD;
- packages relying on Node globals such as `process`, `Buffer`, `__dirname`, or `require` after bundling;
- packages which cannot be bundled without external runtime dependencies other than `@nexmud/api`;
- dependency graphs exceeding NexMUD resource limits.

## 17.3 Compatibility stages

Compatibility validation occurs at multiple layers:

```text
package.json validation
        |
        v
pnpm restore validation
        |
        v
TypeScript diagnostics
        |
        v
esbuild resolution / bundling
        |
        v
artifact static validation
        |
        v
Jint parse/module validation
        |
        v
runtime load/hot-reload
```

Errors must identify the stage and dependency responsible.

Example:

```text
Package "sharp" is not compatible with the NexMUD Jint runtime.
Reason: dependency contains native Node addon "sharp.node".
Imported by: combat-tools -> image-helper -> sharp
```

Do not report a generic build failure when the incompatibility can be identified.

---

# 18. Build Pipeline

Replace the current external-`tsc`-only authoring build with a first-class `ScriptBuildService`.

## 18.1 Build stages

```text
1. Read package.json
2. Validate NexMUD metadata
3. Verify dependency state / lockfile
4. TypeScript project analysis
5. Discover public exports
6. Bundle package entrypoint + dependencies
7. Validate bundled artifact compatibility
8. Produce source map
9. Produce immutable CompiledScriptPackage
10. Store build artifact by content hash
11. If enabled + active runtime profile, hot-reload
12. Publish diagnostics/status
```

## 18.2 Type checking

Use the same pinned TypeScript toolchain as the language server.

The build must honor the package's `tsconfig.json` while enforcing NexMUD-required constraints through an effective generated configuration.

NexMUD-required constraints include at least:

- strict type checking by default;
- runtime target compatible with the configured Jint ECMAScript level;
- no emit from the typecheck step;
- source maps enabled for the bundle;
- `@nexmud/api` resolvable without network installation;
- test-only types excluded from production build unless explicitly imported by production source.

## 18.3 Bundling

Use a pinned local bundler such as esbuild.

Initial production artifact recommendation:

```text
platform = neutral
format = esm
target = es2022
bundle = true
sourcemap = external or embedded in artifact metadata
external = ["@nexmud/api"]
```

Prefer a single production entry bundle initially.

Single-bundle output simplifies Jint loading and prevents unresolved npm module paths from leaking into runtime.

## 18.4 Public exports

Only entrypoint exports are callable by Automation.

`ScriptPackageBuildResult.ExportedFunctions` should be derived from the actual entrypoint TypeScript API/declaration graph rather than every file in the package.

## 18.5 Last-known-good deployment

Preserve and strengthen current behavior:

- failed build does not unload current good runtime;
- failed runtime initialization does not replace current good runtime;
- replacement engine starts successfully before atomic swap;
- late callbacks from old engine are ignored/cancelled by existing runtime generation/instance semantics.

## 18.6 Build cache

Artifact cache key includes at least:

- source content;
- resolved lockfile/dependency graph hash;
- effective `package.json` NexMUD metadata;
- TypeScript version;
- bundler version;
- NexMUD script API version;
- build options.

Do not return a cached artifact built against a different dependency graph.

---

# 19. TypeScript Language Service Architecture

The browser-only Monaco TypeScript worker is insufficient for the required experience once real filesystem packages and npm dependencies exist.

Use a real filesystem-aware TypeScript language service process.

Recommended architecture:

```text
Monaco
  |
  | LSP messages
  v
Monaco language client
  |
  | trusted local bridge
  v
StudioLanguageServiceHost (.NET)
  |
  | stdio JSON-RPC
  v
typescript-language-server
  |
  v
tsserver / TypeScript
  |
  +-- package tsconfig
  +-- real source files
  +-- node_modules declarations
  +-- generated @nexmud/api declarations
```

A direct `tsserver` adapter is acceptable if materially simpler and fully implements the required language features. Do not implement a homegrown TypeScript parser/completion engine.

## 19.1 Process lifetime

One TypeScript language-service process per active Studio profile workspace.

Start lazily when Scripts is first opened or a script file is requested.

Stop it when:

- profile changes;
- Studio closes;
- user invokes Restart TypeScript Language Service;
- process crashes and controlled restart occurs.

Do not spawn one language server per file.

## 19.2 SDK projection

Project-aware tooling must resolve `@nexmud/api` like a normal type package.

Materialize a generated type-only SDK projection beneath the profile workspace, for example:

```text
scripts/.nexmud/sdk/@nexmud/api/
  package.json
  index.d.ts
```

The effective TypeScript config resolves `@nexmud/api` to this local projection.

This replaces the current Monaco-only `addExtraLib` as the authoritative project-level SDK typing mechanism.

The Monaco extra-lib may remain as a bootstrap fallback, but it must not be the only SDK typing path.

## 19.3 Test support projection

If `@nexmud/test` is introduced, materialize its types similarly:

```text
scripts/.nexmud/test-support/@nexmud/test/
  package.json
  index.d.ts
```

## 19.4 Language features required

Before the TypeScript authoring slice is accepted, all of the following must work on real package files:

- syntax highlighting;
- syntax diagnostics;
- semantic diagnostics;
- IntelliSense/completions;
- completion details/docs;
- hover;
- signature help;
- go to definition;
- go to type definition where available;
- find references;
- rename symbol;
- document symbols/outline;
- workspace symbols;
- import completion for local files;
- import completion/types for installed npm dependencies;
- `@nexmud/api` IntelliSense;
- diagnostics after unsaved edits;
- diagnostics after dependency install/update.

---

# 20. Monaco Architecture

Monaco remains local and embedded through Avalonia WebView, but the responsibilities of the current bridge must be narrowed and clarified.

## 20.1 Ownership

C# owns:

- filesystem persistence;
- package catalog;
- profile session;
- toolchain processes;
- LSP process;
- build/test/runtime commands;
- dirty-state policy;
- file watcher;
- profile switching;
- security policy.

Monaco owns:

- text buffer editing;
- cursor/selection;
- editor rendering;
- local editor commands;
- editor decorations;
- syntax token rendering;
- model events.

The language server owns semantic language intelligence.

## 20.2 Script document URIs

Use canonical file URIs for real script files so the Monaco/LSP document identity matches the filesystem project graph.

Example:

```text
file:///Users/.../NexMUD/profiles/avendar/scripts/combat-tools/src/main.ts
```

This URI exists only inside the trusted local Studio/editor/LSP boundary. It is not sent to any remote service.

The current custom `nexmud://profile/...` URI should not remain the authoritative script URI if it prevents ordinary TypeScript project resolution.

Automation documents may continue using non-file identities because they are not source files.

## 20.3 WebView network boundary

The Monaco WebView must load only local NexMUD assets.

Do not permit arbitrary web navigation.

The presence of file URI strings in Monaco models does not grant the page arbitrary filesystem access. Actual read/write authority remains in C#.

---

# 21. Monaco Model Lifecycle

Model lifecycle must be explicit and testable.

Introduce a `StudioTextModelRegistry` or equivalent service which is the authoritative mapping between open source documents and Monaco models.

## 21.1 Open

When opening a script file:

1. Resolve profile/package/file identity.
2. Read source through `IScriptWorkspaceFileService`.
3. Construct canonical file URI.
4. If model already exists, focus it.
5. Otherwise create model with language inferred from extension.
6. Open LSP text document.
7. Record disk content hash/version as the clean baseline.
8. Restore optional cursor/view state.

Do not create duplicate Monaco models for the same canonical file.

## 21.2 Edit

On model change:

- mark document dirty relative to saved baseline;
- send incremental/full LSP change notification as required by client implementation;
- update tab dirty state;
- do not write to disk automatically unless a future autosave feature is explicitly enabled.

## 21.3 Save

On Save:

1. Read current Monaco model text.
2. Write atomically through filesystem service.
3. Update saved baseline hash/version.
4. mark clean;
5. send LSP did-save;
6. do not automatically run arbitrary package scripts;
7. build-on-save remains a user preference, not an architectural requirement.

## 21.4 Save All

Save all dirty documents in the current profile.

Structured Automation documents and script files participate in one Save All orchestration, but each uses its own persistence service.

Report partial failure without losing successfully saved documents.

## 21.5 Close

Closing a dirty file prompts according to the standard dirty-document policy:

```text
Save / Don't Save / Cancel
```

After close:

- send LSP did-close;
- dispose model if no editor group references it;
- remove it from registry;
- retain lightweight view-state metadata if desired.

## 21.6 Rename / move

Before rename/move:

- resolve dirty state;
- request LSP `willRenameFiles` edits if supported;
- apply returned edits transactionally where possible;
- perform filesystem move through C# service;
- update open document identity and tabs;
- recreate/rebind Monaco model with new canonical URI if Monaco cannot change model URI;
- preserve unsaved text only through an explicit controlled migration;
- send LSP rename notifications.

Do not leave a model under an old URI pointing at a moved file.

## 21.7 Delete

Deleting an open file requires confirmation.

On confirm:

- close model;
- delete through filesystem service;
- notify LSP/file watcher;
- update references/problems.

## 21.8 External filesystem changes

Add profile-scoped file watching with debouncing.

If disk changes and the model is clean:

- reload automatically;
- preserve cursor where possible.

If disk changes and the model is dirty:

show a non-destructive conflict banner:

```text
File changed on disk.
[Compare] [Reload from Disk] [Keep Editor Version]
```

Never silently overwrite unsaved editor changes.

## 21.9 Profile switch

Before switching profile:

1. inspect all dirty Automation/workflow/script documents;
2. prompt Save All / Discard / Cancel;
3. cancel old-profile background operations;
4. increment Studio session generation;
5. stop old profile LSP;
6. close/dispose all old profile Monaco models;
7. clear old profile tabs/problems/test results/search state;
8. load new profile workspace;
9. start LSP lazily;
10. ignore all late results carrying the old session generation.

This is mandatory. Cross-profile model leakage is a correctness bug.

---

# 22. Filesystem Service

Evolve the current `ScriptWorkspaceService` rather than letting GUI code perform direct filesystem mutations.

Recommended split:

```text
ScriptWorkspaceService (facade)
|
+-- ScriptWorkspaceFileService
|   +-- read/write/create/delete/move
|   +-- atomic writes
|   +-- containment/security
|   +-- watcher
|
+-- ScriptPackageCatalog
|   +-- discover package.json
|   +-- package identity
|   +-- metadata validation
|
+-- ScriptPackageManager
|   +-- pnpm restore/update/clean
|
+-- ScriptBuildService
|   +-- typecheck/bundle/artifact
|
+-- ScriptTestService
|   +-- test discovery/execution
|
+-- ScriptRuntimeDeploymentService
    +-- enable/disable/load/reload
```

Keep a facade during migration so existing runtime callers do not require one giant rewrite.

## 22.1 Containment

Retain and strengthen current `CombineInside` behavior.

All package file operations must remain inside the package root except explicit workspace-managed files.

Resolve symlinks where necessary to prevent path escape.

Do not permit user-controlled package paths to escape the profile scripts root.

---

# 23. Testing Architecture

Testing is a first-class Scripts activity feature.

## 23.1 User experience

The bottom `Tests` panel and optional Tests section in Scripts explorer show:

```text
combat-tools
  combat.test.ts
    PASS should flee under 20%
    FAIL should not flee while resting
```

Required operations:

- Run All Tests
- Run Package Tests
- Run File Tests
- Run Selected Test
- Re-run Failed
- Cancel
- click failure -> open source location

Debugging tests is not required in the first testing slice, but the architecture must not preclude it.

## 23.2 Test file convention

Default discovery:

```text
test/**/*.test.ts
test/**/*.spec.ts
src/**/*.test.ts
src/**/*.spec.ts
```

Allow later configuration through `nexmud.test` metadata if needed.

## 23.3 Controlled test runner

NexMUD invokes the pinned bundled test runner directly with a generated configuration.

Do not run the package's `npm test` script.

A package's `scripts.test` field may exist but is ignored by the initial Studio Test command.

## 23.4 NexMUD API test shim

Tests which import `@nexmud/api` require a deterministic local shim.

Recommended support package:

```text
@nexmud/test
```

Conceptual usage:

```ts
import { describe, expect, test } from "vitest";
import { createNexMudTestHost } from "@nexmud/test";
import { shouldFlee } from "../src/combat";

const host = createNexMudTestHost({
  character: { health: { current: 20, maximum: 100 } }
});
```

The exact helper API should be designed narrowly around deterministic SDK testing.

## 23.5 Runtime parity gate

Node-based unit tests alone do not prove Jint compatibility.

Therefore `Build` must still perform Jint artifact compatibility validation after tests or independently of them.

A package may pass unit tests and still fail build compatibility if it depends on Node-only runtime behavior.

## 23.6 Test isolation

Tests must not:

- send commands to the live MUD session;
- modify the live mapper;
- mutate live script storage;
- use the current production Jint package instance.

Use test fixtures/fakes.

---

# 24. Runtime Integration

The Scripts authoring environment and production script runtime are separate systems connected only through immutable build artifacts.

```text
Workspace source
      |
      v
Build artifact
      |
      v
Artifact validation
      |
      v
Runtime deployment
      |
      v
JintScriptRuntime
```

## 24.1 Package runtime states

Expose a clear package lifecycle:

```text
Disabled
Needs Restore
Needs Build
Building
Build Failed
Ready
Loading
Running
Faulted
Stopping
```

Do not overload one enum if authoring/build state and Jint runtime state are semantically distinct. Prefer a composed snapshot:

```csharp
ScriptPackageStatus
  DependencyStatus
  BuildStatus
  RuntimeStatus
```

## 24.2 Enable package

Enabling a package:

1. validates metadata;
2. requires/restores dependencies if explicitly initiated or prompts the user;
3. requires a successful build;
4. loads the artifact through the existing capability-checked host;
5. marks runtime state Running only after successful startup.

Do not enable a broken package by silently falling back to source execution.

## 24.3 Disable package

Disabling:

- stops accepting new invocations;
- unloads Jint instance;
- disposes subscriptions/timers through existing runtime ownership;
- persists disabled state.

## 24.4 Manual Run

Manual Run applies to an exported function or package-supported manual entrypoint, not arbitrary raw source text.

Use existing `ManualScriptRun` provenance where applicable.

If the Studio profile is not the active game runtime profile, manual live run must be disabled with a clear explanation. Build and tests remain available.

---

# 25. Profile Context Semantics

The Studio profile selector is an authoring context selector.

On Studio open, default it to the current active connection profile.

Changing Studio profile must not unexpectedly reconnect or change the live game connection.

The header/runtime status must make the distinction obvious:

```text
Profile: Test Character            Runtime: Not active
```

or:

```text
Profile: Avendar                   Runtime: Active / Connected
```

Build and test work for inactive profiles.

Live Run/Enable operations require the matching active runtime profile.

This avoids an editor navigation action causing game-session side effects.

---

# 26. Search

Search spans all authoring forms within the active profile.

## 26.1 Automation search

Search fields include:

- names;
- patterns;
- commands;
- event names;
- groups;
- conditions;
- action values;
- folder names.

## 26.2 Workflow search

Search:

- workflow name;
- trigger;
- step contents;
- referenced script functions.

## 26.3 Script search

Use filesystem/text search service, not Monaco model-only search.

Search includes files not currently open.

Exclude `node_modules`, `.nexmud`, and build output by default.

Results open directly to source location.

A future ripgrep bundle is acceptable but not required if a performant managed implementation is sufficient.

---

# 27. Quick Open and Command Palette

## 27.1 Quick Open

Quick Open should search:

- Automation items;
- workflows;
- script package files;
- script symbols when language service is available.

Result prefixes may distinguish types, but users should not need to know internal IDs.

## 27.2 Command Palette

Expose Studio actions through a searchable command registry.

Do not hard-code command behavior separately in menu handlers, toolbar handlers, and keyboard handlers.

Recommended concept:

```csharp
StudioCommand
  Id
  Title
  Category
  CanExecute(context)
  ExecuteAsync(context)
  DefaultGesture
```

This command system should integrate with the broader NexMUD keybinding architecture rather than create another independent shortcut engine.

---

# 28. Diagnostics and Error Model

The current generic `Error: The operation has timed out.` experience is unacceptable because it lacks context and recovery.

Introduce structured diagnostics.

## 28.1 Diagnostic categories

```text
Studio
Filesystem
Package
Dependency
TypeScript
Build
Compatibility
Test
Runtime
Automation
Workflow
Language Service
Toolchain
```

## 28.2 Diagnostic shape

Recommended common model:

```csharp
public sealed record StudioDiagnostic(
    string Code,
    StudioDiagnosticSeverity Severity,
    string Message,
    string? ProfileId = null,
    string? PackageId = null,
    string? FilePath = null,
    int? Line = null,
    int? Column = null,
    string? AutomationId = null,
    string? OperationId = null,
    IReadOnlyList<StudioDiagnosticAction>? Actions = null);
```

## 28.3 Actionable errors

Examples:

```text
TypeScript language service did not start within 8 seconds.
[Restart Language Service] [View Output]
```

```text
Dependencies are out of date for combat-tools.
package.json changed after pnpm-lock.yaml was generated.
[Install Dependencies]
```

```text
Build failed: package "sharp" requires a native Node addon which cannot run in Jint.
[Open package.json] [View Dependency Chain]
```

```text
main.ts changed on disk while you have unsaved edits.
[Compare] [Reload] [Keep Editor Version]
```

## 28.4 Problems panel

Problems aggregates current unresolved diagnostics.

Requirements:

- group by package/file or Automation item;
- error/warning/info counts;
- double-click opens source/entity;
- stale diagnostics disappear when the underlying problem is resolved;
- operation failures are not permanently retained after successful retry unless explicitly kept in Output history.

## 28.5 Output panel

Output is an append-only operational log for the current Studio session with selectable channels:

```text
Studio
Packages
Build
Tests
TypeScript
Runtime
```

Verbose compiler/process output belongs here, not in modal dialogs.

## 28.6 Runtime panel

Runtime shows:

- active runtime profile;
- loaded Automation runtime generation;
- loaded script package instances;
- start/stop/reload state;
- last runtime fault;
- recent invocation diagnostics;
- capability denials.

## 28.7 Events panel

Events is observational/debugging UI.

Bound memory usage and retain the existing maximum-row concept.

---

# 29. Operation Model and Concurrency

Introduce operation identity and session generation.

Recommended:

```csharp
StudioOperationContext
  OperationId
  ProfileId
  SessionGeneration
  CancellationToken
```

Every async result applied to the UI must verify it still belongs to the active session generation.

This prevents late Profile A results from mutating Profile B after a switch.

## 29.1 Process cancellation

Package/build/test/LSP helper processes must terminate on cancellation or Studio shutdown.

Do not leave orphan Node processes after closing the Studio.

## 29.2 Per-workspace serialization

Serialize mutations which can conflict:

- dependency install/update;
- package delete/rename;
- workspace migration;
- build artifact deployment.

Read-only operations may run concurrently where safe.

## 29.3 Build coalescing

If repeated edits request auto-build, coalesce to the newest build request rather than queueing stale builds indefinitely.

Manual Build remains explicit and never silently cancelled by a background build without status feedback.

---

# 30. Component Boundaries

The final design should move `AutomationStudioWindow` toward shell composition rather than business logic.

Recommended boundaries follow.

## 30.1 GUI project

`NexMud.Gui/AutomationStudio`

```text
AutomationStudioWindow
StudioShellViewModel / Controller
StudioActivityRail
StudioExplorerHost
StudioDocumentHost
StudioBottomPanel
StudioStatusBar

Activities/
  AutomationActivity
  WorkflowActivity
  ScriptActivity
  SearchActivity
  RuntimeActivity

Automation/
  AliasEditor
  TriggerEditor
  SemanticTriggerEditor
  KeybindingEditor
  TimerEditor
  StateRuleEditor
  HighlightEditor
  ConditionEditor
  ActionEditor

Workflow/
  WorkflowEditor
  WorkflowStepEditor

Scripts/
  ScriptExplorer
  ScriptEditorHost
  PackagePropertiesEditor
  TestExplorer
```

Avoid another 50K+ line monolithic `AutomationStudioWindow.cs`.

## 30.2 Client orchestration

`NexMud.Client`

```text
Studio/
  StudioProfileSession
  StudioDocumentSessionStore
  AutomationOrganizationStore
  StudioDiagnosticHub

Scripting/
  ScriptWorkspaceService              // compatibility facade
  ScriptWorkspaceFileService
  ScriptPackageCatalog
  ScriptPackageStateStore
  ScriptBuildCoordinator
  ScriptRuntimeDeploymentService
```

## 30.3 New tooling project

Recommended new project:

```text
src/NexMud.Scripting.Tooling/
```

Responsibilities:

- bundled toolchain location;
- process execution;
- pnpm orchestration;
- TypeScript/LSP process host;
- esbuild orchestration;
- controlled test-runner orchestration;
- process output parsing;
- compatibility scanning.

This project must not reference Avalonia or Jint internals.

## 30.4 TypeScript project

`NexMud.Scripting.TypeScript` remains responsible for engine-neutral TypeScript contracts and build artifact semantics.

It may delegate process execution to tooling abstractions.

Do not let UI code instantiate `ProcessStartInfo("tsc")`.

## 30.5 Jint project

`NexMud.Scripting.Jint` remains the runtime adapter.

Enhance only where required to consume the new bundled artifact format or perform compatibility validation.

Do not move pnpm/Node concepts into Jint.

---

# 31. Suggested Service Interfaces

These are directional contracts, not mandatory exact signatures.

```csharp
public interface IStudioProfileSession
{
    StudioProfileContext Current { get; }
    Task<ProfileSwitchResult> SwitchAsync(string profileId, CancellationToken ct);
}

public interface IAutomationCatalog
{
    Task<AutomationSnapshot> LoadAsync(string profileId, CancellationToken ct);
    Task SaveAsync(string profileId, AutomationDefinition definition, CancellationToken ct);
    Task DeleteAsync(string profileId, string automationId, CancellationToken ct);
}

public interface IAutomationOrganizationStore
{
    Task<AutomationOrganization> LoadAsync(string profileId, CancellationToken ct);
    Task SaveAsync(string profileId, AutomationOrganization organization, CancellationToken ct);
}

public interface IScriptWorkspaceFileService
{
    Task<IReadOnlyList<WorkspaceEntry>> ListAsync(string packageId, string relativePath, CancellationToken ct);
    Task<string> ReadTextAsync(string packageId, string relativePath, CancellationToken ct);
    Task WriteTextAtomicAsync(string packageId, string relativePath, string content, CancellationToken ct);
    Task CreateFileAsync(...);
    Task CreateDirectoryAsync(...);
    Task MoveAsync(...);
    Task DeleteAsync(...);
}

public interface IScriptBuildService
{
    Task<ScriptBuildResult> BuildAsync(string profileId, string packageId, CancellationToken ct);
}

public interface IScriptTestService
{
    Task<IReadOnlyList<ScriptTestCase>> DiscoverAsync(string profileId, string packageId, CancellationToken ct);
    Task<ScriptTestRunResult> RunAsync(ScriptTestSelection selection, CancellationToken ct);
}

public interface ITypeScriptLanguageService : IAsyncDisposable
{
    Task StartAsync(string workspaceRoot, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task RestartAsync(CancellationToken ct);
}
```

Keep interfaces focused. Avoid a single `StudioService` with dozens of unrelated methods.

---

# 32. Script Package Creation

New Package should create a useful conventional project immediately.

Example:

```text
combat-tools/
  package.json
  tsconfig.json
  src/
    main.ts
  test/
  README.md
```

Initial `src/main.ts`:

```ts
import { nex } from "@nexmud/api";

export async function run(): Promise<void> {
  await nex.log.info("combat-tools run");
}
```

Do not create placeholder imports which fail type checking.

The package creation flow should ask only:

```text
Package name
Optional description
```

Advanced permissions can be configured immediately after creation through Package Properties.

---

# 33. Package Properties UX

`package.json` remains editable as text, but common NexMUD metadata must also have a structured package properties editor.

Required fields:

- package name;
- version;
- entrypoint;
- NexMUD API version;
- capabilities/permissions;
- enabled runtime state shown separately;
- dependency status;
- build status;
- test status.

Changes made through structured UI write valid `package.json` atomically.

If `package.json` contains unknown fields, preserve them.

Do not reserialize destructively and discard metadata NexMUD does not understand.

---

# 34. Dependency UX

Initial dependency management may be intentionally modest.

Minimum viable workflow:

1. user edits `package.json` dependencies or uses an Add Dependency control;
2. Studio detects package metadata change;
3. package state becomes `Needs Restore`;
4. user invokes Install/Restore;
5. pnpm updates/restores workspace;
6. TypeScript language service refreshes;
7. Build becomes available.

A richer npm registry search UI is future work.

Do not delay foundational package support to build a marketplace browser.

---

# 35. Source Control / External Editor Interoperability

Although NexMUD does not provide Git UI in this slice, script files must remain friendly to ordinary external tooling.

Required:

- stable directory structure;
- real text files;
- UTF-8;
- conventional `package.json`;
- conventional `tsconfig.json`;
- conventional test file layout;
- no opaque binary source database.

Provide `Reveal in Finder`.

A later `Open in External Editor` command can use OS file associations or configured editor preference.

---

# 36. Migration From Current Studio

Migration must be incremental and reversible during development.

## 36.1 Existing Automation definitions

Do not rewrite all Automation persistence at once.

Migration steps:

1. Load existing definitions using current settings/runtime code.
2. Ensure every definition has a stable ID.
3. Persist IDs using existing supported fields or a catalog wrapper.
4. Initialize organization metadata at root folders.
5. Preserve all runtime semantics.
6. Move UI navigation to ID-based references.
7. Only then remove index-based Studio identity.

## 36.2 Existing script packages

Current package:

```text
scripts/<packageId>/
  manifest.json
  main.ts
  ...
```

Migration rules:

- if `package.json` already exists, validate it and do not overwrite it;
- if only `manifest.json` exists, create a `package.json` mapping the current package id/name/version/entrypoint/capabilities;
- set `nexmud.id` to the existing stable package ID;
- map current capabilities to stable permission strings;
- migrate current enabled state into profile package state;
- do not move source files automatically merely to achieve a prettier layout;
- a migrated package may retain `main.ts` at package root with `nexmud.entry = "./main.ts"`;
- new packages use `src/main.ts`;
- retain legacy `manifest.json` for one compatibility window or archive it under `.nexmud/legacy/` after successful conversion;
- migration must be idempotent;
- migration must never delete source on failure.

## 36.3 Existing compiler

Keep the existing compiler path operational until the bundled tooling build path passes its acceptance tests.

Then switch the default compiler through dependency injection/configuration.

Do not simultaneously delete the old path and introduce the new path before validation.

After migration is proven, remove the external `tsc` fallback from production packaging so a clean machine exercises the bundled path.

## 36.4 Existing Monaco bridge

Do not rewrite Monaco and Studio shell in one commit.

The current bridge remains usable while the new shell/activity model lands.

Later replace/extend the bridge with the LSP-capable model/document protocol.

## 36.5 Existing Studio window

`AutomationStudioWindow` should be hollowed out progressively:

```text
current monolith
   |
   +-- extract profile/session controller
   +-- extract activity shell
   +-- move Automation navigation/editor
   +-- move Scripts navigation/editor
   +-- move bottom diagnostics
   +-- delete obsolete Inspector layout
```

Do not replace the entire file with a speculative new framework in one pass.

---

# 37. Incremental Implementation Sequence

This sequence is mandatory unless a concrete implementation blocker justifies a documented deviation.

Each phase must compile, run, and pass its acceptance gate before the next phase begins.

Each phase should be delivered in small coherent commits. Do not implement the entire ARD in one giant change.

## Phase 0 — Characterization and safety net

Purpose: protect existing behavior before restructuring.

Implement tests for:

- current Automation save/load;
- existing package discovery;
- file create/rename/delete containment;
- build last-known-good behavior;
- Jint reload behavior;
- current Automation-to-script function references;
- Studio document dirty/close semantics where testable.

Add a concise repository document describing current Studio/package paths and migration fixtures.

**Stop gate:** all current tests pass plus new characterization tests.

## Phase 1 — Studio shell and profile session

Purpose: fix the workbench shape without changing scripting/package semantics.

Implement:

- activity rail;
- contextual explorer host;
- document host;
- bottom panel collapsed by default;
- remove permanent Details sidebar;
- profile-scoped Studio session controller;
- session generation/cancellation;
- ID-based document infrastructure;
- preserve current Monaco/package implementation behind Scripts activity;
- preserve current structured Automation editors behind Automations activity.

Do not add npm tooling yet.

**Acceptance gate:** current functionality remains usable; switching profiles cannot leak tabs/documents; bottom panel no longer permanently consumes layout; no Automation object opens as a fake source file.

## Phase 2 — Automation organization and structured UX

Purpose: make ordinary Automation pleasant before deep script tooling work.

Implement:

- stable Automation IDs migration;
- folder organization store;
- Automation explorer folders;
- quick create flows;
- refined condition/action editors;
- drag/drop or move command;
- search/filter within Automations;
- `Open Definition` for script references using existing script package metadata.

Do not change Automation runtime/compiler semantics unless required for correctness.

**Acceptance gate:** user can create, organize, edit, duplicate, enable/disable, and delete each Automation type without touching source code.

## Phase 3 — Script filesystem workbench

Purpose: establish correct filesystem UX before package/toolchain expansion.

Implement:

- conventional Scripts explorer;
- file/folder create/rename/move/delete/duplicate;
- tabs/dirty handling;
- atomic save;
- file watcher and external change handling;
- Reveal in Finder;
- Quick Open for files;
- current script packages still use legacy manifest/build path internally.

**Acceptance gate:** Studio can safely manage nested real source trees with no Monaco model leakage or data loss.

## Phase 4 — Self-contained tooling distribution

Purpose: eliminate reliance on system tooling.

Implement:

- `NexMud.Scripting.Tooling` project or equivalent boundary;
- bundled Node/pnpm/TypeScript/LSP/esbuild/test components;
- `ToolchainLocator`;
- `ToolchainHealthService`;
- macOS packaging changes;
- architecture/platform selection;
- process runner with cancellation and sanitized environment;
- clean-machine verification script.

Do not enable npm package dependencies in runtime yet.

**Acceptance gate:** on a machine/environment with no usable system Node/npm/pnpm/tsc, NexMUD can invoke its bundled TypeScript compiler/toolchain and Monaco still loads from local assets.

## Phase 5 — `package.json` and pnpm workspace migration

Purpose: replace proprietary package metadata while retaining current script execution semantics.

Implement:

- profile `pnpm-workspace.yaml`;
- controlled `.npmrc`;
- common lockfile;
- package catalog based on `package.json`;
- legacy `manifest.json` migration;
- stable `nexmud.id`;
- separate runtime enabled state;
- Install/Restore/Update/Clean commands;
- lifecycle scripts disabled;
- dependency-source validation.

Keep build restricted to source which current runtime can execute until Phase 7.

**Acceptance gate:** existing packages migrate without source loss; new package uses `package.json`; restore works with bundled pnpm; no lifecycle script executes.

## Phase 6 — Real TypeScript language service + Monaco repair

Purpose: make the editor actually behave like a TypeScript IDE.

Implement:

- filesystem-aware TypeScript LSP/tsserver process;
- Monaco language client bridge;
- canonical file URIs;
- `@nexmud/api` SDK projection;
- project-aware `tsconfig` resolution;
- package dependency type resolution;
- syntax highlighting verification;
- completion;
- hover;
- signature help;
- diagnostics;
- go-to-definition;
- references;
- rename;
- symbols;
- LSP restart/recovery;
- model lifecycle integration.

**Acceptance gate:** a package with two TS files and an installed pure-TS npm dependency receives correct highlighting, IntelliSense, cross-file navigation, dependency types, `@nexmud/api` types, and live diagnostics before save.

## Phase 7 — npm-aware production build pipeline

Purpose: make package dependencies executable in Jint safely.

Implement:

- TypeScript typecheck through bundled toolchain;
- esbuild production bundling;
- dependency graph hash;
- compatibility checks;
- Node/native/global rejection diagnostics;
- Jint artifact validation;
- entrypoint export discovery;
- source maps;
- immutable artifact cache;
- last-known-good deployment.

**Acceptance gate:** a pure npm dependency can be imported, bundled, built, and executed through Jint; a Node-only/native package is rejected before runtime with an actionable diagnostic; failed build does not replace the active runtime.

## Phase 8 — First-class testing

Purpose: make script development safe and productive.

Implement:

- test discovery;
- bundled controlled test runner;
- generated configuration;
- `@nexmud/api` test shim;
- optional `@nexmud/test` fixture library;
- Tests UI;
- run/re-run/cancel;
- source navigation;
- test diagnostics;
- no access to live game state.

**Acceptance gate:** test results appear in the Studio, failures navigate to correct TypeScript location, tests do not touch live runtime, and production compatibility validation remains independent.

## Phase 9 — Structured workflows

Purpose: replace raw/awkward workflow authoring with structured step composition.

Implement:

- Workflow activity;
- folder organization;
- structured step editor;
- reorder/duplicate/delete;
- current AutomationAction step types;
- trigger/condition editor;
- legacy workflow adapter;
- round-trip migration safety.

**Acceptance gate:** existing workflows remain behaviorally equivalent; new workflows can be created without writing DSL text; unconvertible legacy workflows are preserved without destructive rewrite.

## Phase 10 — Runtime/diagnostics consolidation and cleanup

Purpose: remove transitional architecture after all replacement paths are proven.

Implement:

- unified diagnostic hub;
- Runtime activity polish;
- Problems/Output/Tests integration;
- remove obsolete combined explorer;
- remove obsolete Inspector code;
- remove legacy index document IDs;
- retire legacy manifest source of truth;
- retire external system `tsc` production dependency;
- remove stale Monaco bridge operations no longer needed;
- documentation update.

**Acceptance gate:** no old Studio code path is required for normal authoring; migration fixtures still load; packaged app passes clean-machine scenario.

---

# 38. Commit Discipline for the Implementation Agent

The implementation agent must not produce a single sweeping rewrite.

Required discipline:

```text
Phase -> tests -> small implementation -> tests -> acceptance check -> commit
```

Prefer commits such as:

```text
studio: add profile-scoped session generation
studio: introduce activity rail shell
automation: migrate studio document identity to stable ids
scripts: add filesystem explorer move/rename service
scripting-tooling: bundle and locate node toolchain
scripts: migrate legacy manifest to package.json
studio: connect monaco to typescript language server
scripts: add npm-aware bundle build
scripts: add controlled test runner
workflow: add structured step editor
```

Avoid commits named `rewrite studio` or `implement new automation architecture` containing unrelated cross-layer changes.

If a phase reveals a prerequisite not covered by this ARD, implement the smallest prerequisite and document why before proceeding.

---

# 39. Error-State UX Requirements

## 39.1 Toolchain unavailable

Scripts activity remains open and shows:

```text
NexMUD scripting tools are unavailable.
The bundled toolchain could not be validated.

[Retry] [View Details]
```

Base client remains usable.

## 39.2 Language server crashed

Editor text remains editable.

Show status:

```text
TypeScript language service stopped unexpectedly.
[Restart]
```

Do not discard models or edits.

## 39.3 Dependency restore failed

Package remains editable.

Mark package `Restore Failed` / `Needs Restore`.

Build may continue only if required dependency graph is already valid; otherwise block with explicit reason.

## 39.4 Build failed

Keep last-known-good runtime.

Problems opens only according to user action/preference.

Runtime status must distinguish:

```text
Running previous build
Current source build failed
```

## 39.5 Runtime reload failed

Report:

```text
Build succeeded, but the new runtime failed to start.
Previous runtime is still running.
```

This distinction is important.

## 39.6 Profile switch with dirty files

Prompt once for all dirty documents, with review option:

```text
Save All
Discard All
Review Changes
Cancel
```

Do not emit a sequence of dozens of individual modal dialogs.

## 39.7 Deleted/renamed external file

If clean, close/update automatically with notification.

If dirty, preserve buffer and ask user where to save/recover.

---

# 40. Performance Requirements

The Studio should remain responsive with realistic large profiles.

Targets are implementation guidance rather than contractual benchmarks:

- opening Studio shell should not wait for pnpm restore or language server startup;
- Automation activity should become interactive before Scripts tooling initializes;
- package discovery should be incremental and cancellable;
- language server starts lazily;
- dependency directories are not walked into UI trees;
- file watcher events are debounced/coalesced;
- full workspace text search excludes `node_modules` and build cache;
- Problems rendering is virtualized or bounded when result counts become large;
- event logs remain bounded;
- large files do not block UI while loading/saving.

Do not optimize by moving correctness-critical persistence into Monaco or WebView-local state.

---

# 41. Security and Trust Boundaries

The Studio is an authoring environment which can retrieve untrusted npm package content. Treat package contents as untrusted.

## 41.1 Install boundary

- disable lifecycle scripts;
- no shell invocation;
- constrain supported dependency sources;
- store dependencies under NexMUD-owned workspace/store;
- sanitize process environment;
- never pass game credentials/tokens to package-manager processes.

## 41.2 Build boundary

- build processes receive source/dependencies, not live game secrets;
- bundler output is validated;
- no arbitrary package command execution.

## 41.3 Runtime boundary

- Jint retains no CLR access;
- capabilities remain server-side/C# enforced;
- npm package code receives only what bundled script code can access through the NexMUD SDK;
- Node APIs are absent at runtime;
- runtime cannot see `node_modules` directly;
- filesystem/network access remain unavailable unless explicitly architected later.

## 41.4 WebView boundary

- editor assets are local;
- restrict arbitrary navigation;
- validate bridge protocol/messages;
- do not expose general-purpose host invocation from JavaScript;
- use narrow typed bridge commands.

---

# 42. Testing Strategy for the NexMUD Implementation

This ARD requires product tests in addition to user-script tests.

## 42.1 Unit tests

Cover:

- package.json parse/validation;
- manifest migration;
- permission mapping;
- path containment;
- Automation folder organization;
- stable ID migration;
- profile session generation;
- dirty document state;
- operation cancellation;
- dependency-source policy;
- compatibility diagnostic mapping;
- build cache key calculation;
- workflow legacy conversion.

## 42.2 Integration tests

Cover:

- create package -> save source -> build;
- install a known pure JavaScript dependency -> build -> run in Jint;
- reject a fixture which imports `node:fs`;
- reject a fixture which requires a native addon;
- successful hot reload;
- failed reload preserves old package;
- LSP finds `@nexmud/api`;
- LSP finds local module definition;
- LSP finds installed dependency declaration;
- rename symbol produces expected edits;
- file rename updates editor identity;
- external file change conflict handling;
- profile switch tears down old language service/models;
- test-runner results map back to source.

## 42.3 Packaged-app tests

On macOS packaged application:

- start with a `PATH` which has no system node/npm/pnpm/tsc;
- open Studio;
- create package;
- receive TypeScript IntelliSense;
- build package;
- run tests;
- install a harmless registry dependency when network is available;
- build/run package through Jint;
- quit and verify no orphan Node/LSP processes.

## 42.4 Migration fixtures

Keep fixture copies of:

- legacy `manifest.json` package;
- nested source package;
- package with existing enabled state;
- existing Automation definitions without IDs;
- legacy workflow DSL;
- malformed package manifest;
- stale lockfile;
- unsupported Node package.

Migration tests must be idempotent.

---

# 43. Acceptance Criteria

The complete architecture is accepted when all of the following are true.

## 43.1 Workbench UX

- Studio has separate Automations, Workflows, Scripts, Search, and Runtime activities.
- The permanent Details sidebar is gone from the default experience.
- Bottom panel defaults collapsed.
- The top toolbar is no longer a row of unrelated actions.
- Profile selector changes the entire Studio context.
- Dirty-state handling is consistent across structured and script documents.
- No Automation definition opens as a blank TypeScript editor unless the user explicitly navigates to actual referenced code.

## 43.2 Automations

- All current Automation types have structured editors.
- Conditions/actions are editable without raw JSON.
- Automation folders work and are distinct from runtime Group.
- Stable IDs survive reorder/delete/rename.
- Script references can navigate to real function definitions.
- Existing Automation runtime behavior remains compatible.

## 43.3 Workflows

- Workflows use structured step authoring.
- No graph editor is required.
- Current workflow semantics can be represented or safely preserved as legacy.
- Unconvertible legacy workflows are not destroyed.

## 43.4 Scripts workspace

- Files and folders are real filesystem entries.
- Create/rename/move/delete works for nested paths.
- `package.json` is the package source of truth.
- New packages have conventional layout.
- Existing packages migrate without source loss.
- Reveal in Finder works.

## 43.5 Self-contained tooling

- A packaged NexMUD app can build scripts with no system Node/npm/pnpm/tsc installed.
- Bundled toolchain version is pinned and discoverable.
- Tooling failure does not prevent the game client from launching.

## 43.6 npm/pnpm

- Profile uses a pnpm workspace and lockfile.
- Registry dependencies restore through bundled pnpm.
- lifecycle scripts do not run.
- unsupported dependency sources are rejected clearly.
- local `workspace:*` package dependencies are supported or explicitly disabled until a documented subsequent slice; no ambiguous partial support.

## 43.7 TypeScript/Monaco

- `.ts` syntax highlighting works.
- IntelliSense works for local files.
- IntelliSense works for `@nexmud/api`.
- IntelliSense works for installed dependency types.
- hover, signature help, definition, references, rename, symbols, and diagnostics work.
- unsaved changes participate in language diagnostics.
- profile switching disposes old models/language service cleanly.
- external file changes do not overwrite dirty buffers.

## 43.8 Build/runtime

- pure npm dependencies can be bundled into Jint-compatible artifacts.
- Node-only/native dependencies are rejected before deployment.
- runtime still executes through Jint.
- capability checks remain authoritative in C#.
- runtime does not load from `node_modules` directly.
- failed build/reload preserves last-known-good runtime.
- source maps report useful TypeScript locations.

## 43.9 Testing

- tests are discoverable in Studio.
- run all/package/file/test/re-run-failed/cancel work.
- failures navigate to source.
- tests cannot mutate the live game runtime.
- passing tests do not bypass Jint compatibility validation.

## 43.10 Diagnostics

- no contextless timeout error is considered acceptable when operation context is available.
- Problems entries are actionable and navigate to source/entity.
- Output captures compiler/package/test/tooling detail.
- stale problems are removed after successful recovery.
- runtime/build distinction is visible.

---

# 44. Explicit Rejections

The implementation agent must not choose the following shortcuts:

```text
- Do not put every entity back into one giant explorer tree.
- Do not make Automation definitions raw TypeScript documents.
- Do not store source code in SQLite to simplify tabs.
- Do not use Monaco as persistence authority.
- Do not rely on user's installed tsc/node/npm/pnpm.
- Do not add a shell/terminal to solve package management.
- Do not execute npm lifecycle scripts.
- Do not execute package.json scripts as the initial Build/Test implementation.
- Do not run user packages in Node instead of Jint.
- Do not polyfill Node wholesale inside Jint.
- Do not expose CLR to npm code.
- Do not identify Automation documents by list index.
- Do not conflate folders with Automation Group.
- Do not replace the current running package before a new artifact starts successfully.
- Do not create one language server per source file.
- Do not load node_modules into the Explorer tree.
- Do not initialize heavy script tooling merely to open the Automations activity.
- Do not combine all implementation phases into one PR/patch.
- Do not touch Jev behavior in this work.
```

---

# 45. Recommended Repository Evolution

A likely final tree after completion:

```text
src/
  NexMud.Gui/
    AutomationStudio/
      Shell/
      Activities/
      Automation/
      Workflow/
      Scripts/
      Diagnostics/
      Monaco/

  NexMud.Client/
    Studio/
    Automation/
    Scripting/

  NexMud.Scripting/
  NexMud.Scripting.TypeScript/
  NexMud.Scripting.Tooling/
  NexMud.Scripting.Jint/

scripts/
  prepare-monaco-assets.mjs
  prepare-scripting-toolchain.mjs
  build-macos-app.sh
  verify-packaged-toolchain.sh

docs/
  architecture/
    NexMUD-automation-studio-ux-workspace-package-tooling-typescript-testing-runtime-architecture.md
```

Do not mechanically create directories which do not improve ownership. The important requirement is clean responsibility boundaries.

---

# 46. Implementation Notes Against Current Code

The following current code should be treated deliberately.

## `AutomationStudioWindow.cs`

Retain as the window entry point initially, but move session state, activity selection, package operations, and editor orchestration out of it phase by phase.

It should eventually compose controls/services, not implement every Studio behavior.

## `StudioModel.cs`

Retain useful tab-set behaviors, but replace `StudioDocumentKind`/index assumptions with explicit document IDs and profile-scoped session models.

`StudioUiPreferences` should evolve to store activity/sidebar/bottom panel state, with the bottom panel default collapsed.

## `AutomationDocumentEditor.cs`

Preserve its structured-editor direction.

Refactor per-kind builders into smaller editor components as the UX evolves.

Do not throw this work away merely because the surrounding Studio shell is poor.

## `ScriptWorkspaceService.cs`

Preserve its profile scoping, atomic writes, containment checks, last-known-good build/deployment behavior, and facade role.

Split responsibilities behind it over time.

Replace proprietary manifest discovery after package migration.

## `MonacoEditorHost.cs`

Preserve a narrow typed bridge.

Evolve it into an editor/LSP transport host rather than expanding it into package/build/runtime ownership.

Remove the assumption that a custom `nexmud://` URI is the best identity for real filesystem script files.

## `nexmud-editor-bridge.js`

Replace browser-worker-only TypeScript intelligence with the LSP-backed model.

Keep local syntax/tokenization and editor interactions.

Do not rely on `addExtraLib` as the only project typing strategy.

## `TypeScriptCompiler.cs`

Replace environment `tsc` lookup with injected bundled tooling.

Evolve the output path into typecheck + bundling rather than raw multi-module `tsc` emission for npm-aware packages.

## `JintModuleLoader.cs`

Do not teach it to resolve arbitrary `node_modules` trees at runtime.

Prefer build-time bundling so Jint sees a controlled artifact with only `@nexmud/api` external.

## `AutomationIr.cs`

Treat its structured conditions/actions as the canonical vocabulary for visual Automation and initial workflow steps.

Extend deliberately rather than inventing a second Studio-only action model.

---

# 47. Final Architecture

The target system is:

```text
                         NEXMUD AUTOMATION STUDIO

+----------------------------------------------------------------------------+
|                              Studio Shell                                  |
|  Profile | Activities | Documents | Commands | Bottom Panels | Status      |
+------------------+-------------------+-------------------+-------------------+
                   |                   |                   |
                   v                   v                   v
          +----------------+   +----------------+   +----------------+
          |  Automations   |   |   Workflows    |   |    Scripts     |
          | structured UI  |   | structured UI  |   | filesystem IDE |
          +-------+--------+   +-------+--------+   +--------+-------+
                  |                    |                     |
                  v                    v                     v
          Automation catalog     Automation IR       Workspace files
                  |                    |                     |
                  +----------+---------+                     |
                             |                               v
                             |                      package.json / pnpm
                             |                               |
                             |                               v
                             |                       TypeScript LSP
                             |                               |
                             |                               v
                             |                         Build + Tests
                             |                               |
                             +---------------+---------------+
                                             |
                                             v
                                  immutable script artifacts
                                             |
                                             v
                                     capability validation
                                             |
                                             v
                                          Jint
                                             |
                                             v
                                      NexMUD Script SDK
                                             |
                                             v
                                  existing domain services
```

The final governing rule is:

> The Studio makes simple automation simple, makes complex automation understandable, and makes actual code feel like real TypeScript development, while all production execution continues through NexMUD's controlled Jint runtime and existing domain boundaries.

Implementation should proceed phase-by-phase. Do not attempt to make the current screenshots superficially prettier while retaining the same underlying workbench model, and do not attempt to replace the entire system in one patch.
