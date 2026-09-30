# NexMUD semantic parser inventory

Status: v0.31.0 acceptance inventory

The canonical gameplay semantic boundary is:

`GameObservation -> Avendar adapter parsers -> typed events -> StateReducer -> consumers`.

## Authoritative gameplay text parsing

All authoritative Avendar transcript-to-domain parsing is under
`src/NexMud.Adapters/Avendar`. This includes prompt/resource fields, room and
entity observations, movement, scans, group snapshots, effects, combat,
item identification, equipment changes, loot/corpse transitions, and stable
character status/progress evidence.

`GameFrameAssembler` under `src/NexMud.Adapters/Observation` owns decoded input
framing and immutable observation creation. It does not own Avendar semantics.

`AvendarPromptStreamProcessor` remains only behind the presentation
`AvendarPromptDisplayFilter`; it controls prompt rendering/slurping and is not a
Core/Mapper/Codex/HUD state parser.

## Consumer inventory

- `MainWindow` contains no gameplay-domain transcript parser. Its regular-expression
  option is transcript search only.
- GUI output-rule regular expressions are presentation-only validation/matching.
- Core state owns reducers, not transcript parsing.
- Mapper (`NexMud.Client/Navigation`) consumes typed room/movement/scan state and
  events. No Mapper-owned gameplay transcript regex remains.
- Codex (`NexMud.Client/Knowledge/WorldKnowledgeStore`) consumes typed room,
  entity, item, corpse, movement, and state observations. Its remaining regex is
  whitespace normalization for persisted/searchable text, not gameplay semantics.
- Automation regular expressions are user-authored raw-text trigger/condition logic.
  They are explicitly allowed and consume raw source observations, never transformed
  WorldBuffer presentation text.
- HUD/gameplay projections consume reducer state/events; they do not parse rendered
  transcript text.
- Transcript/output processing owns presentation transforms only.

## Allowed remaining text matching

Text matching after v0.31.0 is intentionally limited to:

1. Avendar adapter semantic parsing.
2. Raw-text Automation triggers and user-defined script logic.
3. Output highlight/transformation rules.
4. Transcript/search and persistence normalization.
5. Presentation-only prompt rendering policy.

None of these paths is a second authoritative Mapper, Codex, HUD, or MainWindow
state parser.
