# NexMUD Input, Keybinding, and Output Transformation Architecture

**Status:** Proposed implementation architecture  
**Date:** 2026-09-28  
**Product:** NexMUD  
**Scope:** Standard MUD-client interaction parity: input processing, command history/completion, keybindings, output transformation, highlights, gag/substitution, capture handling, scrollback/search integration, and notification hooks.  
**Depends on:**  
- NexMUD Scripting Platform Architecture  
- NexMUD Jint Integration Architecture  
- NexMUD Scripting Vertical Slice Architecture  
- NexMUD Scripting SDK and Automation Migration Architecture  
- NexMUD Mapper Orchestration Architecture  

**Jev constraint:** Design for future Jev integration, but do not migrate or extend Jev behavior in this slice.

---

# 1. Decision

NexMUD will introduce a first-class interaction pipeline for input and output rather than continuing to accumulate UI-local behavior.

The input path becomes:

```text
Keyboard / Paste / UI Action
        |
        v
Input Buffer
        |
        v
Keybinding Resolution
        |
        v
History / Completion
        |
        v
Command Parsing
        |
        v
Alias / Automation Resolution
        |
        v
Command Dispatcher
        |
        v
Transport
```

The output path becomes:

```text
Transport
   |
   v
Telnet / MCCP / GMCP / ANSI Decode
   |
   v
Raw Logical Line/Event Stream
   |
   +-------------------+
   |                   |
   v                   v
Semantic Parsing   Output Transformation
   |                   |
   v                   v
Core State         Highlight / Gag /
                   Substitute / Capture
                           |
                           v
                     World Buffer
                           |
                           v
                         UI
```

The design must preserve two separate concerns:

1. semantic processing for application behavior;
2. visual/output transformation for the player.

A gagged line may still carry semantic meaning.

A substituted line must not rewrite the underlying semantic event stream.

---

# 2. Goals

This slice must provide standard client behavior expected by experienced MUD users:

- configurable keybindings and hotkeys;
- command history;
- tab completion over prior input/output tokens;
- reverse completion with Shift-Tab;
- configurable command separator;
- reliable multi-command parsing;
- input echo styling;
- output highlighting;
- gag/suppress rules;
- substitutions;
- regex and capture groups;
- trigger-safe transformation order;
- searchable scrollback;
- timestamp rendering;
- split-output support;
- copy/select behavior;
- notification/beep hooks;
- script/Automation integration;
- deterministic processing;
- no UI-thread blocking.

It must also define stable boundaries so future packages, plugins, custom UI, and Jev can consume the same interaction primitives.

---

# 3. Non-Goals

This slice does not:

- redesign the Mapper;
- migrate Jev execution;
- add `nex.jev`;
- add a package manager;
- add multiple simultaneous sessions;
- implement full profile management;
- add arbitrary custom UI widgets;
- add a complete script IDE;
- replace semantic parsing with regex triggers;
- expose Transport directly to scripts;
- make transformed display text the source of truth for Core state.

---

# 4. Architectural Principle

The primary rule is:

> Raw game meaning and rendered player presentation are separate pipelines.

The same incoming line may:

- update Core state;
- fire a semantic event;
- trigger Automation;
- be highlighted;
- be substituted;
- be gagged from display;
- contribute tokens to completion;
- be logged.

These operations must not overwrite one another's source data.

---

# Part I: Input Architecture

# 5. Input Sources

All command-producing user interactions normalize into a common input request.

Sources include:

```text
keyboard entry
paste
history recall
keybinding action
UI button
Automation
script
Mapper
future Jev
```

Conceptually:

```text
InputRequest
  SourceKind
  Text
  Timestamp
  CorrelationId
  SessionId
```

Manual text entry remains distinct from generated commands for provenance.

---

# 6. Input Buffer

The UI input buffer owns only editable text state.

It should not own:

- alias execution;
- command splitting;
- history persistence;
- keybinding definitions;
- command dispatch;
- tab-completion data.

Recommended responsibilities:

```text
current text
caret position
selection
editing
IME interaction
paste handling
```

Everything beyond editing should delegate to typed services.

---

# 7. Command Separator

NexMUD must support a configurable command separator.

Example:

```text
n;n;n;w;open chest
```

Default may remain `;` if already established.

The separator belongs to profile/session input configuration.

Required behavior:

```text
input string
    |
    v
command tokenizer
    |
    v
ordered command list
```

Do not implement splitting with naive `string.Split()` if quoting/escaping is supported.

The tokenizer must define escaping behavior.

Recommended initial rules:

```text
separator: configurable single character
escape: backslash
empty commands: ignored
whitespace: trim outer command whitespace
```

Example:

```text
say one\;two;look
```

becomes:

```text
say one;two
look
```

If existing behavior differs, preserve compatibility deliberately.

---

# 8. Input Processing Order

Recommended order for manually submitted text:

```text
Input Buffer
    |
    v
History Record
    |
    v
Command Tokenizer
    |
    v
for each command:
    Alias Resolution
        |
        +--> consumed -> Automation/script actions
        |
        +--> not consumed -> Command Dispatcher
```

Keybindings that directly invoke actions may bypass the editable input buffer but still produce typed commands/actions through the same application services.

---

# 9. Command History

History should become a first-class service.

Conceptual API:

```text
ICommandHistory
  Add(command)
  Previous()
  Next()
  Search(query)
  Snapshot()
```

History is session/profile-aware.

Recommended behavior:

- preserve entered command text;
- avoid duplicate consecutive entries optionally;
- configurable max size;
- Up/Down navigate history;
- restore in-progress buffer after history navigation;
- persistent history is allowed but should be configurable.

Generated Automation/Mapper commands should not pollute manual command history by default.

---

# 10. Tab Completion

NexMUD already requires tab completion over buffer history.

Example:

```text
Laoris hands you the axe Xchilwnsdhg'rta
```

User types:

```text
xc
```

Tab becomes:

```text
Xchilwnsdhg'rta
```

Repeated Tab advances to the next match.

Shift-Tab moves backward.

---

# 11. Completion Sources

Completion should operate over a typed token index built from configured sources.

Recommended initial sources:

```text
recent world output
recent command history
known room occupants
known inventory/equipment names
known exits
```

Do not require Codex integration in this slice.

The completion service should accept pluggable providers later.

Conceptual interface:

```csharp
public interface ICompletionProvider
{
    IEnumerable<CompletionCandidate> GetCandidates(
        CompletionContext context);
}
```

---

# 12. Completion Matching

Initial matching:

```text
case-insensitive prefix
```

Optional later:

```text
fuzzy match
substring
ranked contextual match
```

Do not overbuild fuzzy matching in this slice.

Ranking should prefer:

1. exact case-insensitive prefix;
2. most recently observed;
3. shortest completion;
4. source priority.

Repeated Tab cycles stable results.

Shift-Tab reverses the same candidate set.

---

# 13. Completion Tokenization

Completion tokens must preserve common MUD identifiers:

```text
apostrophes
hyphens
underscores where relevant
```

Example:

```text
Xchilwnsdhg'rta
```

must remain one token.

Avoid generic whitespace-plus-punctuation tokenization that breaks game names.

---

# Part II: Keybindings

# 14. Keybinding Domain

Keybindings are a typed domain model.

Conceptually:

```text
KeybindingDefinition
  Id
  Name
  Gesture
  Context
  Action
  Enabled
  Priority
```

A keybinding does not directly manipulate Avalonia controls.

It invokes application actions.

---

# 15. Supported Actions

Initial actions may include:

```text
SubmitInput
HistoryPrevious
HistoryNext
CompletionNext
CompletionPrevious
ClearInput
FocusInput
ScrollPageUp
ScrollPageDown
ScrollToBottom
SearchScrollback
SendCommand
RunAutomation
TogglePane
ToggleJev
MapperPause
MapperResume
MapperAbort
```

Future actions may include script entrypoints.

---

# 16. Keybinding Context

Bindings must be context-aware to prevent collisions.

Example contexts:

```text
Global
World
Input
Mapper
ScriptEditor
Dialog
```

Resolution order:

```text
most specific context
    |
    v
Global
```

Text-entry controls must not accidentally consume normal typing through global bindings.

---

# 17. Conflict Resolution

Keybinding conflicts must be explicit.

If multiple bindings match:

```text
same gesture
same context
same priority
```

NexMUD should surface a configuration conflict instead of silently choosing one.

---

# 18. Persistence

Keybindings are profile-scoped configuration.

Do not persist them as arbitrary JavaScript.

They may compile/invoke Automation or script actions internally, but the source of truth remains the typed keybinding definition.

---

# Part III: Output Pipeline

# 19. Output Stages

Recommended output pipeline:

```text
Transport bytes
    |
    v
Telnet/MCCP decode
    |
    v
ANSI tokenization
    |
    v
logical output frames/lines
    |
    +----------------------+
    |                      |
    v                      v
Semantic Parsing     Display Transformation
    |                      |
    v                      v
Core State           World Buffer Entry
```

Both branches use the same decoded source frame.

One branch must not depend on the transformed output of the other.

---

# 20. Output Frame

Introduce or normalize around a typed output frame.

Conceptually:

```text
OutputFrame
  FrameId
  Timestamp
  RawText
  PlainText
  AnsiRuns
  PromptFlag
  Source
  Sequence
```

Potential metadata:

```text
IsPrompt
IsSystem
IsLocalEcho
IsReplay
```

This becomes the stable unit for transformation and logging.

---

# 21. Prompt Preservation

Existing NexMUD behavior supports keeping prompts in world output.

Prompt slurp remains configurable.

Prompt handling should occur before final buffer rendering but must preserve semantic prompt events.

A hidden prompt may still update:

```text
HP
Mana
Movement
XP
Room
Exits
Time
```

where protocol/parser semantics provide them.

---

# Part IV: Transformation Rules

# 22. Transformation Domain

Output transformations are typed rules.

Conceptually:

```text
OutputRule
  Id
  Name
  Enabled
  Match
  Actions[]
  Priority
  Scope
```

Match types:

```text
Substring
Regex
SemanticEvent
```

Actions:

```text
Highlight
Gag
Substitute
Capture
Notify
Beep
LogMarker
```

---

# 23. Rule Processing Order

Recommended deterministic order:

```text
source frame
    |
    v
match all applicable rules
    |
    v
captures
    |
    v
substitutions
    |
    v
highlights
    |
    v
gag decision
    |
    v
notifications
    |
    v
rendered buffer entry
```

Priority ordering must be stable.

If existing Automation trigger semantics require trigger evaluation before substitutions, preserve that separation.

---

# 24. Gag / Suppression

A gag rule suppresses display only.

It must not suppress:

- semantic parsing;
- Core state changes;
- Automation triggers unless explicitly configured;
- logging unless logging mode chooses rendered-only output;
- replay source data.

This rule is critical.

---

# 25. Substitution

Substitution changes displayed text.

It must not rewrite:

- raw decoded source;
- semantic parsing input;
- Core state;
- event journal source.

Example:

```text
Original:
A cityguard says 'Halt!'

Display:
[Guard] Halt!
```

Semantic systems still receive the original line.

---

# 26. Highlighting

Highlights operate on rendered spans rather than destructive text rewrites where practical.

Support:

```text
foreground
background
bold
italic where supported
underline
```

ANSI styles and user highlights must compose predictably.

Recommended precedence:

```text
base ANSI
    |
user rule override
```

Exact merge policy must be deterministic.

---

# 27. Capture Groups

Regex captures should be structured data.

Example:

```regex
^(\w+) tells you '(.*)'$
```

Capture result:

```text
1 = speaker
2 = message
```

Automation actions may consume captures without reparsing.

Do not represent capture groups only as mutable global variables.

---

# 28. Trigger Integration

Text triggers and output transformations may share match infrastructure but remain distinct concepts.

A trigger causes behavior.

A transformation changes presentation.

One rule may support both through multiple actions, but internal responsibilities must remain explicit.

Example:

```text
match: "You are hungry."
actions:
  - Highlight
  - Notify
  - SendCommand
```

The send action executes through Automation/Jint.

The highlight action executes through the display transformation pipeline.

---

# 29. Regex Safety

All regex matching should use bounded execution.

Requirements:

```text
explicit timeout
compiled/cache strategy where appropriate
bounded pattern size
structured diagnostics
```

Do not permit pathological regex to block the UI or output processor.

---

# Part V: Searchable Scrollback

# 30. World Buffer

The world view should render from a logical scrollback buffer rather than treating the control itself as the data store.

Conceptual entry:

```text
WorldBufferEntry
  EntryId
  Timestamp
  SourceFrameId
  RenderedText
  StyledRuns
  IsPrompt
  IsLocalEcho
  IsGagged
```

Gagged entries may be omitted from visible buffer while still remaining in source logs depending on configuration.

---

# 31. Scrollback Capacity

Scrollback should be bounded.

Configuration:

```text
max entries
or
max memory budget
```

Prefer memory-aware design over unbounded text accumulation.

Virtualized rendering is strongly preferred.

---

# 32. Search

Search should support:

```text
plain text
case-sensitive toggle
regex optional
next/previous
```

Search runs against visible rendered scrollback by default.

Future option:

```text
search raw source
```

Do not block UI on large buffers.

---

# 33. Split Output

NexMUD should support a split-output mode similar to mature MUD clients.

Behavior:

```text
bottom pane follows live output
top pane remains at historical position
```

The split is a view over the same underlying world buffer.

Do not duplicate buffer contents.

---

# 34. Scroll Lock Behavior

When the user scrolls away from the bottom:

```text
new output arrives
```

the view should not forcibly snap to bottom.

Provide clear affordance:

```text
new output indicator
scroll-to-bottom action
```

---

# Part VI: Timestamps and Local Echo

# 35. Timestamps

Timestamps are rendering metadata, not inserted into source text.

Configurable:

```text
off
time only
time with milliseconds
date + time
```

Search/copy behavior may optionally include them.

Logging configuration determines whether timestamps are emitted in exported logs.

---

# 36. Input Echo

Manual commands may be echoed into the world buffer using a distinct style.

Required metadata:

```text
IsLocalEcho = true
SourceKind = User
```

Generated Automation/Mapper commands may optionally be visible with separate provenance-aware styling.

Do not fake input echo by injecting text into the incoming server stream.

---

# Part VII: Notifications

# 37. Notification Actions

Output/Automation rules may invoke:

```text
in-app notification
system notification
beep/sound
dock/taskbar attention
```

Platform-specific behavior belongs behind an abstraction.

No direct OS notification calls from scripts.

---

# 38. Focus-Aware Behavior

Notification policies may depend on application focus.

Example:

```text
if app unfocused:
    system notification
else:
    in-app marker only
```

This should be configuration, not hard-coded trigger behavior.

---

# Part VIII: Scripting SDK Integration

# 39. New SDK Surface

This slice may introduce limited interaction APIs.

Recommended initial additions:

```ts
nex.input
nex.output
```

Do not expose low-level UI controls.

---

# 40. `nex.input`

Potential initial API:

```ts
interface InputApi {
  history(): readonly string[];
  current(): string;
}
```

Command emission remains through:

```ts
nex.commands
```

Do not create another send-command API.

Keybinding registration should remain a typed configuration domain initially, not arbitrary script registration, unless implementation strongly benefits from script-backed bindings.

---

# 41. `nex.output`

Potential preview API:

```ts
interface OutputApi {
  notify(message: string): Promise<void>;
}
```

Avoid exposing direct buffer mutation initially.

User scripts should not arbitrarily rewrite the world buffer outside the transformation rule model.

---

# Part IX: Automation Integration

# 42. Shared Matching Infrastructure

Alias, trigger, keybinding, and output rules may share parsing/matching primitives.

They should not become one giant undifferentiated rule type.

Recommended separation:

```text
Input Alias
Text Trigger
Semantic Trigger
Output Transformation
Keybinding
```

Shared implementation may include:

```text
regex matcher
capture DTO
condition evaluator
action dispatcher
```

---

# 43. Automation Actions

Existing Automation actions should be extended where needed to support:

```text
Highlight
Gag
Substitute
Notify
Beep
```

However, these actions should target typed output services rather than manipulate Avalonia controls directly.

---

# 44. Command Provenance

Input-generated commands must preserve source:

```text
User
Alias
Keybinding
Automation
Mapper
Script
Future Jev
```

This continues the existing command provenance model.

---

# Part X: Logging Interaction

# 45. Source vs Rendered Logging

The architecture should prepare for two distinct log modes:

```text
Raw/semantic-preserving log
Rendered player-view log
```

This slice does not need the full logging product, but must not make it impossible.

Therefore:

- raw source frames remain available;
- transformed display entries remain available;
- timestamps remain metadata;
- gagging does not destroy source frames.

---

# Part XI: Profile and Session Boundaries

# 46. Scope

All interaction configuration must be capable of becoming profile-scoped later.

This includes:

```text
keybindings
command separator
history settings
completion settings
output rules
timestamp settings
input echo
scrollback size
notification behavior
```

Do not bake these into global static configuration.

---

# 47. Session Isolation

Even before multi-session UI exists, runtime state should be session-aware.

Examples:

```text
history cursor
completion cycle
scroll position
active split
temporary captures
```

These are session state, not global application state.

---

# Part XII: UI Architecture

# 48. World View

The World view becomes a projection over `WorldBuffer`.

It should not own transformation logic.

Responsibilities:

```text
render styled runs
selection
copy
scroll
split view
search result highlighting
new-output indicator
```

---

# 49. Input View

The Input view becomes a projection over:

```text
InputBuffer
HistoryService
CompletionService
KeybindingService
```

It should not own alias parsing or command dispatch.

---

# 50. Settings UI

This slice should expose coherent configuration sections:

```text
Input
  command separator
  history
  completion
  local echo

Keybindings
  bindings
  conflicts
  reset/defaults

Output
  timestamps
  scrollback
  split behavior

Rules
  highlights
  gag
  substitutions
  notifications
```

Do not mix these settings into unrelated Jev/Mapper pages.

---

# Part XIII: Performance

# 51. Output Throughput

Output processing must not block on:

- database writes;
- script compilation;
- UI layout;
- regex without timeout;
- notification delivery.

Recommended pattern:

```text
decode
  |
create immutable frame
  |
semantic + transform processing
  |
append logical buffer
  |
UI observes incremental update
```

---

# 52. Buffer Rendering

Avoid rebuilding the entire transcript on each line.

Use incremental/virtualized rendering.

The historical Mapper beach-ball issue makes UI-thread discipline especially important across NexMUD.

---

# 53. Completion Index

Completion indexing should be incremental.

Do not rescan the entire scrollback buffer on every Tab press.

Maintain a bounded recency index.

---

# Part XIV: Fault Handling

# 54. Transformation Rule Failure

If one output rule fails:

- record diagnostic;
- skip/fail that rule;
- continue processing the line;
- do not lose source output;
- do not disconnect;
- do not block unrelated rules.

---

# 55. Regex Timeout

On timeout:

```text
rule fault diagnostic
rule invocation aborted
output continues
```

Repeated timeouts may disable the rule after a configured threshold.

---

# 56. Keybinding Failure

If a bound action fails:

- surface a non-blocking diagnostic;
- preserve input focus where practical;
- do not swallow unrelated keystrokes.

---

# Part XV: Testing

# 57. Input Tests

Test:

- single command;
- command separator;
- escaped separator;
- whitespace handling;
- alias consumed;
- alias passthrough;
- manual provenance;
- history navigation;
- in-progress buffer restore.

---

# 58. Completion Tests

Test:

- prefix match;
- case-insensitive match;
- apostrophe-containing names;
- repeated Tab cycles;
- Shift-Tab reverses;
- stable candidate set;
- no result leaves input unchanged;
- recent result outranks stale result.

---

# 59. Keybinding Tests

Test:

- context resolution;
- global fallback;
- conflict detection;
- text-entry safety;
- action dispatch;
- disabled binding;
- persistence round-trip.

---

# 60. Output Transformation Tests

Test:

- highlight;
- gag;
- substitution;
- capture;
- multiple rule priority;
- ANSI composition;
- gag does not suppress semantic parsing;
- substitution does not alter source frame;
- trigger behavior independent from rendering.

---

# 61. Scrollback Tests

Test:

- bounded buffer;
- incremental append;
- search next/previous;
- regex search;
- split-output behavior;
- no auto-scroll when user is reviewing history;
- scroll-to-bottom;
- copy/select.

---

# 62. Replay Tests

Recorded output replay must reproduce:

```text
semantic events
transform matches
rendered output
gag decisions
captures
```

given the same configuration.

This provides regression coverage for output behavior without a live MUD.

---

# 63. UI Responsiveness Tests

Stress test:

```text
high output rate
many highlight rules
search active
split view active
completion index updating
```

Expected:

```text
input remains responsive
scroll remains responsive
no synchronous UI-thread blocking
```

---

# Part XVI: Acceptance Criteria

# 64. Input Acceptance

Complete when:

- configurable separator works;
- multi-command parsing is deterministic;
- history is first-class and session-aware;
- Tab completion works from recent output/history;
- Shift-Tab reverses completion;
- command provenance remains correct;
- input echo is styled separately;
- input UI does not own alias/dispatch logic.

---

# 65. Keybinding Acceptance

Complete when:

- bindings are typed/persisted;
- context-aware resolution works;
- conflicts are detected;
- standard history/completion/navigation actions are bindable;
- commands/actions route through application services;
- no direct control manipulation is required for business behavior.

---

# 66. Output Acceptance

Complete when:

- output frames preserve source data;
- semantic parsing and display transformation are independent;
- highlights work;
- gag works;
- substitution works;
- captures work;
- regexes are bounded;
- transformed output renders incrementally;
- gagged output can still affect semantic state and triggers;
- timestamps are metadata-driven;
- local echo is not mixed with incoming transport data.

---

# 67. Scrollback Acceptance

Complete when:

- scrollback is bounded;
- search works;
- split-output works;
- user scroll position is preserved;
- new-output indicator works;
- copy/select remains usable;
- high-volume output does not freeze the UI.

---

# Part XVII: Explicit Stop Point

After this slice, do not move directly to Jev.

The recommended next architecture slice is:

```text
NexMUD Profiles, Sessions, and Connection Management Architecture
```

That should define:

- profile identity;
- character/world configuration;
- connection settings;
- multiple simultaneous sessions;
- per-session scripting/runtime scopes;
- reconnect behavior;
- TLS;
- profile-scoped Automation, scripts, Mapper, keybindings, output rules, and UI layout;
- import/export boundaries.

Jev remains untouched except where it consumes existing stable services.

---

# 68. Roadmap

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
Mapper Orchestration
      |
      v
THIS SLICE:
Input Pipeline
+ Command History
+ Tab Completion
+ Keybindings
+ Output Transformations
+ Highlight/Gag/Substitute
+ Searchable Scrollback
+ Split Output
+ Notifications
      |
      v
Profiles / Sessions / Connection Management
      |
      v
Logging / Replay Productization
      |
      v
User Scripting UX
      |
      v
Packages / Import-Export
      |
      v
Custom UI / Extensibility
      |
      v
Jev Migration LAST
```

---

# 69. Final Architectural Rule

NexMUD should behave like a mature MUD client even when Automation, Mapper, scripts, and Jev are all disabled.

That means input, output, history, completion, keybindings, highlighting, gagging, substitution, scrollback, search, and notifications must be first-class client capabilities.

The interaction layer should therefore remain independent from Jev and reusable by every higher-level subsystem.
