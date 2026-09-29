# NexMUD Default World Design System Implementation

**Release:** 0.18.4  
**Scope:** Default World view only

This implementation is governed by `NexMUD-main-window-prescriptive-visual-spec.md` and intentionally stops before secondary-workspace styling.

## Construction order

1. `NexMudTheme` installs the dark material palette and five accent variants.
2. `NexMudIcons` provides one monochrome vector icon language for game chrome and HUD information.
3. `NexGameFrame`, `NexMajorPanel`, `NexMinorPanel`, `NexPanelHeader`, `NexSectionDivider`, and ornamental corner/junction controls establish physical frame depth.
4. `NexResourceBar` / `NexXpBar` provide substantial semantic meters independent of Fluent `ProgressBar` styling.
5. `CharacterHudPanel` composes identity, crest, level/XP, vitals, status, attributes, and conditions from `CharacterHudViewModel`.
6. `RoomContextPanel` composes room identity, exits, entity groups, observations, memory, and quick actions from `RoomContextViewModel`.
7. `MainWindow` composes the framed World transcript and `NexCommandBar`, plus icon/label application navigation.

## Theme boundary

The five accent variants share the same base materials and semantic status colors:

- Ember / Brass (default)
- Arcane / Cyan
- Mystic / Violet
- Blood / Crimson
- Verdant / Emerald

Accent resources affect selection/focus/primary actions. HP, MA, MV, warnings, errors, and success retain fixed semantic colors.

## Scope guard

No Codex, Mapper, Automation, Scripting, Character-detail, Abilities, or Settings workspace styling is changed in this pass. Those workspaces continue to use the existing application UI until the default World design system is accepted.
