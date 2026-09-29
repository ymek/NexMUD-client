
# NexMUD Main Window: Prescriptive Visual Implementation Specification

The previous implementation is still materially wrong.

Do not reinterpret the attached reference. Treat it as the design target.

This pass is not about information architecture. The layout is already sufficiently understood. The purpose of this pass is to reproduce the **visual language, component styling, decorative framing, icons, theme system, and “real game client” character** visible in the reference.

The current implementation looks like a styled developer tool. NexMUD must look like a finished fantasy game client.

## 1. Reference image is authoritative

Use the attached NexMUD concept image as the primary visual specification.

Specifically reproduce its:

- ornamental outer frame
- layered metallic borders
- corner decoration
- inset panel construction
- dark textured surfaces
- beveled / dimensional appearance
- warm metallic highlights
- iconography
- selected-tab treatment
- status-bar treatment
- command-input treatment
- fantasy-game typography hierarchy
- five accent/theme variants
- HUD composition
- deliberate contrast between game chrome and data surfaces

Do not simplify these elements into generic Avalonia borders.

The reference is intentionally more visually elaborate than the current alpha UI.

## 2. The product must look like a game client

The target is closer to:

- Diablo
- Titan Quest
- Grim Dawn
- Path of Exile launcher/client chrome

combined with:

- modern analytics information density
- restrained contemporary UI typography
- strong tabular organization

It must not resemble:

- Rider
- VS Code
- Grafana
- an admin dashboard
- a terminal emulator
- a debug harness

The fantasy styling should exist primarily in the **frame, chrome, borders, icons, headers, separators, resource bars, and interaction states**.

The actual textual information remains clean and highly legible.

---

# 3. Main visual construction

The main application surface must use several layers.

Do not use:

```text
black background
+ orange 1px border
+ content
```

Use:

```text
outer ornamental frame
    ↓
dark metallic edge
    ↓
warm brass accent
    ↓
inner shadow / recess
    ↓
panel surface
    ↓
content
```

Every major surface should feel physically inset or mounted into the application frame.

## Outer application frame

The entire content region should have a designed frame similar to the reference.

Required visual construction:

- dark near-black outer stroke
- brass/bronze inner stroke
- subtle highlight along upper/left edges
- darker shadow along lower/right edges
- ornamental corner pieces
- small decorative junction elements where major panels meet

Approximate palette:

```text
Frame darkest       #080A0D
Frame metal         #181613
Bronze shadow       #594127
Antique brass       #A97B3F
Brass highlight     #D4AF6A
Bright edge         #E1BF78
```

Do not use bright gold uniformly.

Metal should vary between shadow, body, and highlight.

---

# 4. Border styling is mandatory

The borders in the reference are a major part of the design.

Implement reusable border components rather than generic `Border` controls scattered throughout XAML.

Create components/styles conceptually equivalent to:

```text
GameFrame
MajorPanelFrame
MinorPanelFrame
InsetFrame
SectionDivider
OrnamentalCorner
PanelJunction
```

## Major panel frame

Used for:

- World
- Character
- Room / Context

Visual structure:

```text
2–4 px total perceived frame

outer dark edge
thin brass line
dark separator
inner warm highlight
recessed content surface
```

Corners should not simply be rounded.

Use slight fantasy ornamentation.

The reference uses small angular / engraved / geometric corner details.

Reproduce this with:

- SVG
- Path geometry
- vector assets
- or 9-slice/asset-based frame pieces

Do not substitute a plain `CornerRadius=4`.

## Minor panel frame

Used inside Character and Room/Context.

Examples:

- attribute cells
- entity groups
- room memory
- small state blocks

These should be quieter than major frames:

- dark stroke
- slate surface
- very restrained brass edge
- inner shadow

## Decorative junctions

Where Character and Room/Context meet, use a small decorative horizontal separator similar to the reference.

Likewise for major horizontal panel boundaries.

This matters.

---

# 5. Surface hierarchy

There must be visibly distinct levels of elevation.

Use approximately:

```text
Application background
#080B0F

Primary recessed surface
#0B1015

Secondary panel
#101820

Raised card
#16212B

Interactive raised surface
#1B2934
```

Avoid one giant uniform black background.

Use subtle gradients where practical:

```text
top slightly lighter
bottom slightly darker
```

No strong glossy effects.

The result should feel like dark metal, stone, lacquered wood, or obsidian rather than flat CSS rectangles.

---

# 6. Five accent schemes

The reference visibly presents multiple UI accent choices. Implement this intentionally.

There should be five selectable accent themes using the **same underlying dark base theme**.

Only accent semantics change.

### Theme 1: Ember / Brass

Default.

```text
Primary       #D4A65A
Bright        #F0C675
Dark          #8B5C28
Glow          warm amber
```

This should be the default visual identity.

### Theme 2: Arcane / Cyan

```text
Primary       #22C7D6
Bright        #66E5EF
Dark          #127A86
```

### Theme 3: Mystic / Violet

```text
Primary       #8B6FDB
Bright        #B59AF3
Dark          #514080
```

### Theme 4: Blood / Crimson

```text
Primary       #C64B55
Bright        #EB7680
Dark          #792B33
```

### Theme 5: Verdant / Emerald

```text
Primary       #36A86E
Bright        #63D594
Dark          #236846
```

These are **accent themes**, not five unrelated complete themes.

They affect:

- active navigation
- focus rings
- selected tabs
- selected rows
- decorative highlights
- primary buttons
- certain icons
- selected borders
- subtle glow

They must NOT recolor:

- HP
- MA
- MV
- warning
- error
- success

Semantic colors stay semantic.

---

# 7. Icons are required

Do not use a primarily text-only navigation system.

The reference uses icon + label consistently.

Create or adopt a coherent vector icon set.

All icons must share:

- similar stroke width
- similar optical size
- restrained fantasy/geometric character
- monochrome rendering with theme tinting

Required icons include at minimum:

```text
World
Character
Abilities
Codex
Map
Automation
Scripting
Jev
Settings

Search
History
Previous
Next
Send
Look
Scan
Where
Inventory

HP
MA
MV
XP

Position
Combat
Target
Jev state

People
Objects
Fixtures
Corpses

Strength
Dexterity
Constitution
Intelligence
Wisdom
Charisma
```

Preferred appearance:

- 16–20 px for navigation
- 14–18 px in information panels
- 20–24 px for significant HUD identifiers

Use SVG or Avalonia `PathIcon`.

Do not use emoji.

Do not mix several unrelated icon styles.

---

# 8. Top application chrome

The reference has a deliberate horizontal game-client header.

Reproduce this structure.

Approximate height:

```text
56–64 px
```

Left:

```text
NexMUD mark/logo
```

Center:

```text
icon + World
icon + Character
icon + Abilities
icon + Codex
icon + Map
icon + Automation
icon + Scripting
icon + Jev
```

Right:

```text
connection indicator
session/profile
settings
```

Active navigation:

- slightly raised or inset tab
- theme accent underline or lower edge
- brighter icon
- parchment text
- subtle warm glow

Inactive navigation:

- muted silver/gray icon
- muted parchment label
- no boxed button appearance

Do not make navigation look like HTML links.

---

# 9. NexMUD branding

Do not render `NexMUD` as generic bold sans-serif text.

The brand area should use a display treatment similar to the reference:

```text
Nex MUD
```

with:

- `Nex` parchment/light neutral
- `MUD` default brass accent

Use an appropriate serif/display face available to the application.

Do not compromise the rest of the UI typography to achieve this.

A small geometric compass / nexus / rune-style mark should appear adjacent to the name.

Create this as a vector asset.

---

# 10. World panel

The World panel must be the largest visual object.

It needs a **substantial ornamental frame**.

Panel header:

```text
[icon] WORLD
```

Use:

- serif/display title or strong UI title
- accent-colored icon
- small optional transcript mode/filter controls aligned right

Do not display giant unused header space.

World content surface:

```text
deep recessed charcoal/obsidian
```

not pure black.

Use inner padding of approximately:

```text
20–24 px horizontal
16–20 px vertical
```

Transcript font:

```text
14–16 px default
```

depending on actual font metrics.

Line height approximately:

```text
1.35–1.5
```

The current tiny transcript presentation is unacceptable.

### Transcript color hierarchy

Use ANSI when supplied.

For NexMUD-generated presentation:

```text
normal game text       parchment
user-entered command   cyan
room title             brass/gold
exit keywords          cyan/teal
damage                 ember/red
success                green
system/meta            muted gray
```

Do not over-color ordinary prose.

---

# 11. Command console

The command console should look like a physical interaction strip mounted beneath the transcript.

Approximate height:

```text
52–60 px
```

It must contain:

```text
prompt icon
large command input
history control
previous
next
optional utility/settings
Send
```

Input:

- recessed surface
- 1–2 px cyan/theme focus edge
- 14–16 px monospace text
- generous horizontal padding

Send button:

- visually substantial
- metallic/theme-accent treatment
- slight inner highlight
- darker lower edge
- icon + text or strong arrow treatment

It must not look like a normal form submit button.

---

# 12. Character HUD

The reference Character panel is one of the primary visual anchors.

Reproduce its composition much more literally.

Structure:

```text
CHARACTER
────────────────────────

[crest/emblem]  Character Name
                Level 12 Rogue
                Human

XP  12,647 / 18,000      70%
████████████████░░░░

♥ HP   ███████████████   195/195
◆ MA   ███████████████   148/148
● MV   ███████████████   280/280

Position     standing
Combat       CLEAR
Target       -
Jev          OFF

STR 12   DEX 18   CON 14
INT 13   WIS 11   CHA 10
```

## Character identity area

Use either:

- class crest
- abstract character emblem
- shield/rune icon

Do not use generated character portrait art unless there is a real supported asset mechanism.

The crest should sit inside a decorated medallion/frame approximately:

```text
56–72 px
```

## XP bar

XP must be visually prominent.

Approximate height:

```text
8–12 px
```

Color:

```text
gold/amber
```

Include current / next-level progress and level transition.

## HP / MA / MV

Approximate height:

```text
8–10 px
```

Each has:

- semantic icon
- short label
- substantial bar
- numeric value

Colors:

```text
HP  #E24D5B
MA  #36B9E8
MV  #43BD82
```

Bars should have:

- dark trough
- slightly luminous fill
- subtle top highlight
- rounded or bevel-like ends

Do not render 2 px spreadsheet bars.

## Status block

Use a composed 2x2 grid:

```text
Position | Combat
Target   | Jev
```

Each cell should be an inset mini-card.

## Attributes

Use six compact attribute cells:

```text
STR     12
DEX     18
CON     14

INT     13
WIS     11
CHA     10
```

Each attribute gets a small icon.

Do not use a long property list.

---

# 13. Room / Context panel

Do not create an empty dashboard.

Reproduce the reference's rich composition, but without room artwork.

The absence of room art should be compensated for by **better information density**, not empty space.

Structure:

```text
ROOM / CONTEXT
────────────────────────

The Bar in the Adventurer's Lounge
School of Heroes • Ground Level

EXITS
[N] [E] [W]

PEOPLE 2          OBJECTS 0
• aelin rogue     None

FIXTURES 1        CORPSES 0
• small sign      None

RECENT OBSERVATIONS
• ...
• ...
• ...

ROOM MEMORY
Visits 18
Routes 3
Recurring 5
Last observed ...

QUICK ACTIONS
[Look] [Scan] [Where] [Inventory] [Map]
```

Entity categories should use icons.

Small count badges should be visually obvious.

Empty groups should collapse or become subdued rather than consuming large boxes.

No giant blank panels.

---

# 14. Ornamental styling

The reference uses ornament sparingly but consistently.

Implement ornamental elements at:

- application corners
- World panel corners
- right-rail outer corners
- major horizontal section boundaries
- selected/major header edges

Use:

- angular motifs
- compass/rune geometry
- thin engraved lines
- small diamond or knot junction elements

Do not use:

- vines
- skulls everywhere
- medieval parchment textures
- excessive gothic flourishes

Target:

```text
premium dark fantasy
```

not:

```text
Renaissance fair
```

---

# 15. Texture and image treatment

The reference does not rely solely on flat colors.

Introduce subtle image/texture assets for:

- application frame
- brushed/dark metal
- obsidian/grain
- faint border patina
- subtle vignette

These must remain extremely low contrast.

No repeating obvious texture tiles.

No fake room imagery.

No decorative landscape behind readable application content.

Texture must provide material quality without reducing legibility.

---

# 16. Shadows and lighting

Use subtle directional light.

Typical panel treatment:

```text
top/left: slightly brighter
bottom/right: slightly darker
```

Major frames may have:

```text
soft exterior shadow
small brass inner highlight
recessed inner shadow
```

Do not use large web-style box shadows.

The effect should suggest engraved metal panels.

---

# 17. Typography specification

Use three roles.

### Display

For:

- NexMUD branding
- World
- Character
- Room / Context

Use an elegant fantasy-compatible serif/display face.

Do not use it for dense data.

### Interface

For:

- navigation
- controls
- labels
- metadata
- attributes

Use a clean readable sans-serif.

### Monospace

For:

- transcript
- command input
- scripting/logs later

Use a high-quality monospace.

Hierarchy should approximately be:

```text
Brand             22–26
Major section     18–20
Character name    18–20
Room title        16–18
Nav               13–14
Body UI           13–14
Secondary         11–12
Transcript        14–16
Command           14–16
```

Do not allow 8–10 px UI text except genuinely tiny metadata.

---

# 18. Spacing system

Use a consistent spacing scale.

```text
4
8
12
16
24
32
```

Normal panel padding:

```text
16
```

Major panel padding:

```text
20–24
```

Small card gap:

```text
8
```

Major section gap:

```text
16
```

Do not arbitrarily pack everything together.

---

# 19. Five visual themes must be visible in Settings later

Do not implement the entire Settings screen during this pass, but build the design system so these five accent variants exist from the beginning.

Theme switching should be resource-driven.

Conceptually:

```text
NexMudTheme.Base
NexMudAccent.Ember
NexMudAccent.Arcane
NexMudAccent.Mystic
NexMudAccent.Blood
NexMudAccent.Verdant
```

Do not hard-code gold into every control.

Default is Ember/Brass.

---

# 20. Required reusable Avalonia components

Do not hand-style each instance independently.

Build reusable primitives:

```text
NexGameFrame
NexMajorPanel
NexMinorPanel
NexPanelHeader
NexSectionDivider
NexNavItem
NexIconButton
NexPrimaryButton
NexCommandBar
NexResourceBar
NexXpBar
NexAttributeCell
NexStatusCell
NexEntityGroup
NexQuickAction
NexCountBadge
NexCharacterCrest
```

And centralized resources:

```text
Colors
Brushes
Gradients
Borders
Typography
Spacing
Icons
Theme accents
```

The goal is to establish a real design system rather than a collection of XAML tweaks.

---

# 21. Do not work on secondary screens

This pass ends when this single view is visually correct:

```text
World
+
Character
+
Room / Context
+
Command bar
+
Main navigation
```

Do not spend time styling:

- Codex
- Map
- Automation
- Scripting
- Character detail
- Abilities
- Settings

Their styling will follow once the NexMUD design system is approved.

---

# 22. Required implementation strategy

Before modifying existing controls:

1. Define the theme resource dictionary.
2. Define the five accent dictionaries.
3. Build the border/frame primitives.
4. Build the shared icon library.
5. Build typography styles.
6. Build resource bars.
7. Build the Character HUD.
8. Build Room/Context.
9. Build World frame.
10. Build command console.
11. Compose the main screen.
12. Tune spacing and proportions against the reference screenshot.

Do not start by changing random margins and colors in the existing view.

---

# 23. Visual comparison requirement

Keep the attached reference image open while implementing.

At completion, compare the real application screenshot side-by-side with the reference.

Evaluate specifically:

```text
Does the outer framing look comparable?
Do panels have comparable depth?
Are major borders ornamental?
Does the character HUD have comparable presence?
Are the resource bars substantial?
Is XP prominent?
Are icons used throughout?
Does navigation feel like a game client?
Does the command bar feel designed?
Does the Room panel feel composed?
Is brass used structurally rather than as thin outlines?
Is cyan restrained?
Is typography comparable in scale?
Does the application look expensive/premium?
```

If several answers are “no,” continue the implementation before returning it.

---

# 24. Acceptance criteria

The implementation is not complete merely because all information appears on screen.

It is complete when, viewed at normal desktop scale, someone could reasonably look at the application and the attached concept image and immediately recognize them as the **same design system**.

The desired reaction is:

> “That is the implemented NexMUD interface.”

Not:

> “That contains approximately the same panels.”

Functional similarity is insufficient.

Visual fidelity matters in this task.
