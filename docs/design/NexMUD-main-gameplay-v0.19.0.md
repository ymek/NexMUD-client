# NexMUD main gameplay surface v0.19.0

This release applies the production gameplay-surface correction only. Secondary workspaces remain out of scope.

## Two-tier information model

The World surface now uses two information tiers:

1. Foveal gameplay HUD directly below transcript output and above command entry: level, exact XP values, HP, MA, MV, position, combat, target, and Jev state.
2. Right-side intelligence rail: compact character identity/stats followed by room identity, exits, named entities, observations, room memory, and quick actions.

NexMUD does not infer level-progress percentage from `Experience` plus `Exper/level`. The HUD shows current XP and XP remaining with an indeterminate/neutral progress treatment until game-specific level boundaries are available.

## Generated image asset manifest

Assets live under `src/JevMud.Gui/Assets/Ornaments/` and are packaged as Avalonia resources.

| Asset | Source resolution | Purpose |
| --- | ---: | --- |
| `application-mark.png` | 1024x1024 | NexMUD application/brand mark |
| `character-crest.png` | 512x512 | Character medallion artwork |
| `frame-corner-tl.png` / `tr` / `bl` / `br` | 256x256 each | Major outer frame filigree |
| `panel-corner-tl.png` / `tr` / `bl` / `br` | 256x256 each | Quieter panel filigree |
| `panel-junction.png` | 256x256 | Major frame/panel junction ornament |
| `section-divider.png` | 1024x128 | Character/room and section transition ornament |
| `navigation-active-ornament.png` | 256x256 | Active primary-navigation ornament |
| `jev-sigil.png` | 512x512 | Jev master/state sigil |

Frame/panel corner, junction, divider, active-navigation, and Jev assets use transparent backgrounds. Structural frame lines remain Avalonia drawing primitives.

## Character fields exercised by the populated score fixture

`tests/JevMud.Tests/Fixtures/avendar-score-randolph.txt` exercises:

- Randolph the Smuggler
- human / thief / level 6
- Chaotic Neutral
- STR 15 (base 15)
- INT 15 (base 15)
- WIS 15 (base 15)
- DEX 20 (base 18)
- CON 16 (base 15)
- CHR 15 (base 15)
- HP 195/195
- MA 148/148
- MV 280/280
- Experience 12,858
- 1,727 to level
- Exploration 190
- Hitroll +15
- Damroll +6
- Saves -5
- AC 87 / vulnerable
- Items 14 / 100
- Weight 73 / 352
- Wealth 2g 111c

## Room/context fields exercised by the gameplay fixture

`tests/JevMud.Tests/Fixtures/avendar-room-observations.txt` exercises The Adventurer's Lounge with east/south/west exits, a kankoran student, a small fountain, and a prominent bulletin board. The parser regression verifies the student as an occupant and the fountain/board as fixtures, including fountain drinkability and board targeting.

## Input-mode behavior

`CommandInputPolicy` centralizes presentation behavior by `SessionInputMode`.

- Password mode remains visible and masked.
- Password mode disables history and completion.
- Sensitive input is never persisted to command history.
- Password submission clears the input but retains focus.
- A password-to-normal state transition clears residual text, removes masking, restores the normal placeholder, and explicitly refocuses command input.
- Normal keyboard behavior remains Enter submit, Up/Down command history, Tab next completion, Shift+Tab previous completion.
