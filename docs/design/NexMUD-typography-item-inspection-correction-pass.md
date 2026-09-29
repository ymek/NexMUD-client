# NexMUD Typography + Item Inspection Correction Pass

The latest Character/Inventory workspace is structurally better, but two areas are not production-ready:

1. **Typography is far too small throughout the Character workspace and gameplay HUD.**
2. **Item Inspection is laid out like raw parsed data rather than a usable game UI.**

This pass should correct only those issues.

Do not redesign the overall Character/Inventory layout.  
Do not change parsing/state architecture.  
Do not change the paper-doll slot model.  
Do not change the World transcript layout.  
Do not work on unrelated screens.

The goal is to make the existing layout readable at normal desktop scale and turn Item Inspection into a properly designed ARPG/MUD information panel.

## 1. Typography is currently too small

The UI is being rendered at a scale where important data requires squinting.

The following must be comfortably readable at normal desktop viewing distance:

- paper-doll slot labels
- equipped item names
- character attributes
- inventory items
- inspection metadata
- item modifiers/effects
- HUD labels
- HUD numeric values
- tactical-state labels

Do not solve this by making every element huge. Establish a proper typography hierarchy.

Use approximately:

```text
Character name / room title       20–24 px
Major section title               15–17 px
Paper-doll equipped item          13–15 px
Paper-doll slot label             11–12 px
Character stats                   13–14 px
Inventory items                   13–14 px
Inspection item title             18–20 px
Inspection primary metadata       13–14 px
Inspection secondary metadata     11–12 px
HUD labels                        12–13 px
HUD resource values               12–14 px bold
HUD state values                  12–14 px
Tiny metadata                     never below ~10–11 px
```

Treat these as visual targets, not rigid constants, but the current 8–10 px appearance is unacceptable.

## 2. Use centralized typography resources

Do not patch `FontSize` independently across dozens of controls.

Create or use centralized semantic typography styles such as:

```text
NexTypography.Display
NexTypography.SectionTitle
NexTypography.Body
NexTypography.BodyStrong
NexTypography.Metadata
NexTypography.Small
NexTypography.Mono
NexTypography.Hud
NexTypography.HudStrong
```

The Character workspace, paper doll, Item Inspection, inventory, and gameplay HUD should consume these shared styles.

We need one coherent scale.

## 3. Paper-doll readability

Current slot geometry is acceptable, but text inside the slots is too small.

Each occupied slot should clearly show:

```text
SLOT NAME
item name
```

Example:

```text
MAIN HAND
a copper sword
```

Rules:

- slot label smaller and muted
- item name larger and brighter
- item name must not be rendered at tiny metadata size
- preserve enough vertical padding that text is not cramped
- do not increase slot height excessively
- long item names should wrap to at most two lines
- do not shrink font dynamically to unreadable sizes just to keep one line

Empty slots remain quieter:

```text
HEAD
—
```

but still readable.

## 4. Character summary typography

The lower Character summary block must become easier to scan.

Use:

```text
STR 15   DEX 20 (18)   CON 16 (15)
INT 17   WIS 15        CHR 15
```

with values visually stronger than labels.

Likewise:

```text
Hit +15   Dam +6   Save -5
AC 71 / Vulnerable
Explore 190
```

Do not render all of this at tiny secondary-text size.

## 5. Inventory typography

Inventory entries should use normal body text, not metadata sizing.

Example:

```text
a copper polearm
a copper chainmail shirt
a quilted bedroll
a wooden canoe
a copper sword
a copper dagger
```

Target approximately:

```text
13–14 px
```

The search field should match the application input typography and not look oversized relative to the list.

## 6. Item Inspection requires a complete presentation rewrite

The current Item Inspection surface is effectively dumping parsed fields vertically.

This is wrong.

It should present the item as a structured game object.

Use the available `identify`/Codex information and format it into clear sections.

For the supplied `gem of guarding` example:

```text
Object: a gem of guarding
Flags: rot_death
Weight: 1
Level: 1
Material: ruby

Type: armor
AC: 0

Affects armor class by -3
Affects mana by 2
Affects hp by 3
```

The panel should become something like:

```text
A Gem of Guarding
Floating • Armor

ruby • Level 1 • Weight 1

┌──────────────────────────────┐
│ ARMOR CLASS              0   │
└──────────────────────────────┘

MODIFIERS
Armor Class                -3
Mana                       +2
Hit Points                 +3

FLAGS
rot_death

Observed in current session

[Identify]
```

This is the intended information hierarchy.

## 7. Inspection panel structure

Use these sections:

### Item identity

At the top:

```text
A Gem of Guarding
Floating • Armor
```

Primary title should be large and readable.

Use the normalized display name where available.

### Metadata

Compact secondary line:

```text
Ruby • Level 1 • Weight 1
```

Do not stack each property vertically unless necessary.

### Primary item stats

Use a compact stat row/grid.

Examples:

```text
AC 0
Damage ...
Value ...
```

Only display fields actually available for the item type.

Do not fabricate absent values.

### Modifiers / affects

This is one of the most important sections.

Render as aligned label/value rows:

```text
MODIFIERS

Armor Class      -3
Mana             +2
Hit Points       +3
```

Use semantic coloring where useful:

- beneficial modifier: green/positive
- detrimental modifier: ember/red
- neutral: parchment

Do not blindly infer whether lower/higher is beneficial if the game mechanic is ambiguous. Use explicit domain knowledge where available.

### Flags

Render compactly as chips/tags or concise list:

```text
FLAGS

rot_death
```

Flags must not dominate the panel.

### Source/status

Optional muted metadata:

```text
Observed this session
Identified
```

### Actions

At the bottom:

```text
[Identify]
```

Other actions may be added later only if correctly supported.

## 8. Inspection panel width

The panel is currently too narrow for useful structured presentation.

Do not solve this by shrinking text.

Give Item Inspection enough width to function.

For the Character workspace, preferred division should be approximately:

```text
Paper doll + character/inventory    70–75%
Inspection                          25–30%
```

or whatever produces a practical inspection width.

Set a sensible minimum width for Item Inspection, roughly:

```text
280–340 px
```

depending on window scale.

If the overall workspace becomes too narrow, prefer:

- collapsing inspection into a drawer/detail pane
- or allowing the inventory region to give up width

Do not shrink inspection fonts into unreadability.

## 9. Selected vs hovered inspection

Support both concepts cleanly:

- hover = temporary quick inspection
- selection/click = persistent inspection

When nothing is selected or hovered:

```text
ITEM INSPECTION

Hover an equipped item for a quick view.
Select one for persistent detail.
```

When hovering:

- show quick detail
- do not destroy persistent selection state internally

When clicked:

- pin the item into the inspection panel until another item is selected

This behavior should be reusable between paper doll and inventory.

## 10. Avoid raw parser terminology where possible

Convert parser/domain names into player-facing labels.

Examples:

```text
rot_death
```

may remain as a flag if no user-facing translation exists.

But:

```text
Affects hp by 3
```

should display as:

```text
Hit Points   +3
```

and:

```text
Affects mana by 2
```

as:

```text
Mana         +2
```

Do not make users read parser output when a clean UI label is available.

## 11. Gameplay HUD typography

The HUD layout itself is directionally correct.

Do not redesign it.

Increase legibility.

Target:

```text
LVL 6  NEXT 615 XP  HP [195/195]  MA [148/148]  MV [280/280]
```

Rules:

- `LVL`, `NEXT`, `HP`, `MA`, `MV`: 11–13 px
- numeric values inside bars: 12–14 px, medium/bold
- resource bars: approximately 18–22 px tall
- tactical state text: 11–13 px
- do not rely on tiny text inside thin bars
- keep the HUD compact

The numeric values must remain centered vertically within the bars.

Increase bar height if required for readability.

Do not add extra rows.

## 12. Tactical state typography

The current:

```text
Standing
Clear
No target
Off
```

is too small.

Keep the current layout but increase readability.

Use:

```text
◇ Standing
⚔ Clear
◎ No target
✦ Off
```

with values around:

```text
12–13 px
```

Important states such as:

```text
ENGAGED
STUNNED
JEV ACTING
active target
```

may use stronger weight/color.

Do not enlarge normal quiet states excessively.

## 13. Respect available space

The current solution appears to have optimized for fitting everything by shrinking text.

Reverse that priority.

Priority order:

1. readable typography
2. useful information
3. compact layout
4. maximum information density

If content does not fit:

- scroll
- wrap
- collapse secondary detail
- use a detail pane

Do not reduce primary gameplay text below comfortable readability.

## 14. DPI / Retina behavior

Verify typography on macOS Retina scaling.

Do not validate only from raw pixel dimensions.

Run the application at the normal development display scale and inspect visually.

Controls should remain readable without requiring zoom or leaning toward the display.

## 15. Required screenshot verification

Return screenshots showing:

1. full Character workspace
2. paper doll at normal scale
3. Item Inspection populated from `a gem of guarding`
4. inventory list populated
5. main World view showing the gameplay HUD at normal scale

Do not provide cropped screenshots only.

We need to judge typography relative to the entire interface.

## 16. Acceptance criteria

This pass is complete when:

- Character workspace text is comfortably readable
- paper-doll item names no longer look microscopic
- inventory entries are readable at normal scale
- HUD resource values are clearly readable inside bars
- tactical states are readable without squinting
- Item Inspection no longer resembles raw parser output
- Item Inspection has clear identity, metadata, stats, modifiers, flags, and actions
- Item Inspection has enough width to function
- hover and persistent selection are supported cleanly
- parser names are translated into player-facing labels where appropriate
- no unrelated layouts were redesigned

The goal is not to make the interface larger.

The goal is to establish a **professional readable typography hierarchy** and make Item Inspection feel like part of a real ARPG-inspired MUD client rather than a debug view.

---