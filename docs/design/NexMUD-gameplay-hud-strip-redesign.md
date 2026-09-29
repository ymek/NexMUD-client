

# NexMUD Gameplay HUD Strip Redesign

Redesign only the gameplay HUD strip between the World transcript and command input.

Do not modify the rest of the main window in this pass.

The current HUD is too tall, wastes horizontal space, separates labels from values, and makes gameplay state difficult to scan quickly. The goal is a dense, modern game HUD that keeps critical information directly in the player’s visual path.

The overriding principle is:

> Every pixel in this strip must earn its space.

## 1. Scope

This pass changes only:

- level / XP-to-next-level display
- HP / MA / MV presentation
- Position / Combat / Target / Jev presentation
- spacing, alignment, semantic color, and density of this HUD strip

Do not change:

- World transcript layout
- command input behavior
- right-side Character panel
- Room / Context panel
- navigation
- other workspaces
- parsing/state architecture

Use the existing canonical runtime state.

---

## 2. Target composition

Use this layout as the structural target:

```text
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ LVL 6   NEXT 1460 XP   HP [████████████ 195/195]   MA [████████████ 148/148]   MV [████████████ 280/280]   ◇ Standing   ⚔ Clear │
│                                                                                                             ◎ No target  ✦ Off │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

During combat:

```text
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ LVL 6   NEXT 1460 XP   HP [████████░░░░ 132/195]   MA [████████████ 148/148]   MV [██████████░░ 231/280]   ◇ Standing   ⚔ ENGAGED │
│                                                                                                             ◎ City Guard ✦ ACTING │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

This is one integrated HUD rail.

Do not implement it as five separate cards.

---

# 3. HUD density

The current implementation wastes too much space around labels.

Keep these labels:

```text
LVL
NEXT
HP
MA
MV
```

but place them immediately adjacent to their values.

Correct:

```text
LVL 6
NEXT 1460 XP
HP [████████████ 195/195]
MA [████████████ 148/148]
MV [████████████ 280/280]
```

Incorrect:

```text
HP
[bar]
195 / 195
```

Incorrect:

```text
HP          195 / 195
[bar]
```

Incorrect:

```text
[ HP card ] [ MA card ] [ MV card ]
```

The design must be stingy with whitespace.

### Spacing targets

Use approximately:

```text
label -> value/bar:       4–6 px
group -> group:           12–16 px
HUD exterior padding:     12–16 px
state item gap:           10–14 px
```

Do not use large internal card padding.

---

# 4. Progression

Do not show total lifetime XP.

Show only:

```text
LVL 6
NEXT 1460 XP
```

or inline:

```text
LVL 6   NEXT 1460 XP
```

Do not show:

```text
13,125 XP
```

unless a future feature explicitly needs it.

Do not draw an XP progress bar.

Current data reliably gives XP-to-next-level, but not necessarily enough information to calculate an accurate percentage through the current level.

Do not fabricate a percentage.

### Progression color

Use brass/gold styling:

```text
LVL 6
NEXT 1460 XP
```

with:

- `LVL 6` slightly stronger
- `NEXT 1460 XP` quieter but still readable

Do not over-emphasize progression relative to HP/MA/MV.

---

# 5. Resource bars

Keep explicit labels:

```text
HP
MA
MV
```

The labels are useful, but they must consume almost no space.

Preferred pattern:

```text
HP [████████████ 195/195]
MA [████████████ 148/148]
MV [████████████ 280/280]
```

## Numeric values

The numeric value must appear inside the bar.

Do not place resource numbers:

- above the bar
- below the bar
- in a separate column
- in a separate row

The bar itself is the value container.

### Bar visual treatment

Each bar should have:

- dark recessed trough
- substantial fill height
- subtle highlight
- numeric value rendered inside
- readable contrast whether the fill is full or partial
- minimal border/chrome

Approximate height:

```text
18–24 px
```

Do not make the bars thin telemetry lines.

## Fixed semantic colors

These colors do not change with theme:

```text
HP  crimson / red
MA  cyan-blue
MV  green
```

Suggested values:

```text
HP  #E24D5B
MA  #36B9E8
MV  #43BD82
```

Theme accent must not recolor HP/MA/MV.

---

# 6. Tactical state cluster

Position, Combat, Target, and Jev belong together.

Do not label the area:

```text
STATE
TACTICAL STATE
STATUS
```

The grouping itself should communicate what it is.

Use a compact 2x2 cluster:

```text
◇ Standing    ⚔ Clear
◎ No target   ✦ Off
```

During combat:

```text
◇ Standing    ⚔ ENGAGED
◎ City Guard  ✦ ACTING
```

This cluster should live on the right side of the HUD.

Do not use four large boxed cards.

Use:

- icon
- value
- semantic color
- subtle spacing
- one shared visual region

The cluster may use a very subtle inset background or separator, but should still feel integrated with the full HUD rail.

---

# 7. Position state

Position uses a neutral visual identity under normal conditions.

Examples:

```text
◇ Standing
◇ Resting
◇ Sleeping
◇ Stunned
◇ Incapacitated
```

### Color behavior

Normal:

```text
Standing
```

Use muted steel / silver.

Abnormal states should gain emphasis:

```text
Resting        subdued amber
Sleeping       muted cool tone
Stunned        warning amber
Incapacitated  danger red
```

Do not make `Standing` visually loud.

---

# 8. Combat state

Combat must become visually obvious when active.

Examples:

```text
⚔ Clear
⚔ ENGAGED
⚔ Recovering
```

Use:

```text
Clear       green
Engaged     ember/red
Recovering  amber
```

When combat is `Clear`, keep it subdued.

When `ENGAGED`, make it one of the highest-emphasis elements in the HUD.

This can use:

- brighter color
- stronger font weight
- brighter icon
- subtle glow

Do not flash.

---

# 9. Target state

Examples:

```text
◎ No target
◎ City Guard
◎ Goblin
```

No target:

```text
muted violet-gray
```

Populated target:

```text
violet / arcane purple
```

Suggested target accent family:

```text
#9B7DE3
```

A populated target should be visually noticeable but still subordinate to critical resource depletion or active combat state.

Do not render `Target: -`.

Use:

```text
◎ No target
```

instead.

---

# 10. Jev state

Examples:

```text
✦ Off
✦ Ready
✦ Acting
✦ Deciding
```

Behavior:

```text
Off       muted
Ready     active theme accent
Acting    bright theme accent
Deciding  bright theme accent
```

Jev should remain quiet when off.

It should become visually prominent only when actively involved.

Use the actual Jev icon/sigil if already available in the approved icon system.

Do not invent a new decorative glyph in this pass.

---

# 11. Dynamic emphasis

Do not give every state equal visual prominence.

At rest:

```text
◇ Standing    ⚔ Clear
◎ No target   ✦ Off
```

should be subdued.

During combat:

```text
◇ Standing    ⚔ ENGAGED
◎ City Guard  ✦ ACTING
```

the important values should become visually stronger.

If the player is stunned:

```text
◇ STUNNED     ⚔ ENGAGED
◎ City Guard  ✦ Off
```

`STUNNED` should immediately attract attention.

The visual hierarchy should be exception-driven.

---

# 12. Layout proportions

Use approximate horizontal allocation:

```text
Progression     12–15%
HP              18–20%
MA              18–20%
MV              18–20%
Tactical state  25–30%
```

These are guidelines, not rigid percentages.

The key requirements are:

- bars remain wide enough to read quickly
- resource groups remain tightly packed
- tactical state remains readable
- progression stays compact

Do not let the progression block consume excessive width.

---

# 13. Height

Target total HUD height:

```text
44–54 px
```

Maximum:

```text
~60 px
```

The HUD should fit visually as one compact rail with a two-line tactical cluster on the right.

Do not grow the full HUD height just because tactical state uses two rows.

Use careful alignment so the left side remains visually centered.

---

# 14. Relationship to transcript and command input

The player’s visual path must remain:

```text
latest transcript output
↓
HUD
↓
command input
```

The HUD must sit immediately below the transcript.

The command input must sit immediately below the HUD.

Do not add large vertical gaps.

Do not add decorative padding between these three regions.

This proximity is intentional: HP/MA/MV and combat state must remain visible during tunnel-vision gameplay.

---

# 15. Visual style

Match the established NexMUD visual system:

- obsidian / midnight base
- subtle recessed HUD background
- parchment text
- brass/gold progression
- semantic resource colors
- restrained theme accents
- subtle separators
- strong legibility
- no heavy box borders around each item
- no admin-dashboard appearance

The whole strip should feel like one mounted ARPG instrument rail.

Use separators sparingly.

Good:

```text
LVL 6   NEXT 1460 XP   HP [...]   MA [...]   MV [...]   ◇ Standing   ⚔ Clear
```

Bad:

```text
[ LVL CARD ][ HP CARD ][ MA CARD ][ MV CARD ][ STATE CARD ]
```

---

# 16. Responsive behavior

When horizontal space becomes constrained:

1. preserve HP / MA / MV first
2. preserve Combat state
3. preserve Position
4. preserve Target if populated
5. preserve Jev if active
6. allow progression text to compact
7. allow `NEXT 1460 XP` to become `NEXT 1460`
8. do not remove resource values

If absolutely necessary at narrow widths, tactical state may stack slightly tighter.

Do not move HP/MA/MV back to multiple rows.

Do not hide the numeric values.

---

# 17. Accessibility and readability

Ensure:

- text over bars remains readable at all fill levels
- colors are not the only carrier of meaning
- labels remain present for HP / MA / MV
- icons remain paired with textual state
- state changes remain understandable without relying solely on color

Example:

```text
⚔ ENGAGED
```

not just a red sword icon.

---

# 18. Acceptance criteria

The HUD redesign is complete only when all of the following are true:

- total XP is removed
- `LVL <n>` is shown
- `NEXT <n> XP` is shown
- no XP progress bar is present
- `HP`, `MA`, and `MV` labels remain
- labels sit directly beside their bars
- HP/MA/MV numeric values appear inside the bars
- there is no separate resource-number row
- bars are substantial enough to read peripherally
- Position / Combat / Target / Jev form one coherent 2x2 state cluster
- no `STATE` heading exists
- Combat changes semantic color by condition
- Target is muted when empty and highlighted when populated
- Jev is muted when off and prominent only when active
- Position is quiet when normal and emphasized when abnormal
- entire HUD remains compact
- total height is approximately 44–54 px
- HUD sits directly between transcript and command input
- no other main-window areas were modified

---

# 19. Final target wireframe

Use this as the final structural reference:

```text
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ LVL 6   NEXT 1460 XP   HP [████████████ 195/195]   MA [████████████ 148/148]   MV [████████████ 280/280]   ◇ Standing   ⚔ Clear │
│                                                                                                             ◎ No target  ✦ Off │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Combat example:

```text
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ LVL 6   NEXT 1460 XP   HP [████████░░░░ 132/195]   MA [████████████ 148/148]   MV [██████████░░ 231/280]   ◇ Standing   ⚔ ENGAGED │
│                                                                                                             ◎ City Guard ✦ ACTING │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

The key principle is density without ambiguity.

Do not optimize for decorative whitespace. Optimize for immediate gameplay readability.