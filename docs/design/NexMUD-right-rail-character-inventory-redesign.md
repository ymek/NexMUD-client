
# NexMUD Right Rail + Character/Inventory Workspace Redesign

Redesign the right-side gameplay rail and introduce a dedicated Character/Inventory workspace.

Do not modify the World transcript, gameplay HUD strip, command input, top navigation, mapper, automation, scripting, Codex internals, or protocol architecture in this pass.

This work has two goals:

1. turn the current right sidebar into a compact, useful gameplay intelligence rail
2. create a proper Character/Inventory surface with an ARPG-inspired equipment “paper doll” adapted for a text MUD

The design principle remains:

> Every pixel must earn its space.

Do not create another dashboard.

---

## 1. Right rail purpose

The right rail exists to answer:

- who am I?
- what am I wearing?
- where am I?
- where can I go?
- who is here?
- what can I interact with?
- what just changed?

It is not the place for every available character or room field.

Stable character information belongs at the top.

Dynamic room information should receive more vertical space beneath it.

Contextual actions belong at the bottom.

Target structure:

```text
┌────────────────────────────────────┐
│ Character summary                  │
│ Compact loadout                    │
├────────────────────────────────────┤
│ Current room                       │
│ Exits                              │
│ Visible entities                   │
│ Recent observations                │
│ Room memory                        │
├────────────────────────────────────┤
│ Context actions                    │
└────────────────────────────────────┘
```

---

# 2. Character summary

Replace the current verbose Character block with a compact identity/stat summary.

Use the actual character name as the section title.

Do not render redundant headings such as:

```text
CHARACTER
Randolph the Smuggler
```

Prefer:

```text
Randolph the Smuggler
Human • Thief • Level 6
Chaotic Neutral
```

Then a compact stat matrix:

```text
STR 15    DEX 20    CON 16
INT 16    WIS 15    CHR 15
```

Retain current/base values internally.

Where useful, show modified values compactly:

```text
DEX 20 (18)
CON 16 (15)
```

Do not discard the base value.

Then compact combat/support stats:

```text
Hit +15    Dam +6    Save -5
AC 79 / Vulnerable
Explore 190
```

Then:

```text
Wealth 2g 1s 120c
Items 17/100    Weight 85/352
```

Use alignment and grouping rather than large cards.

---

# 3. Compact equipment/loadout summary

Equipment is useful and must remain available in the gameplay rail.

However, do not render the current long prose dump of every slot.

Show only the most useful equipped items in a compact loadout section.

Example:

```text
LOADOUT

Main     copper sword
Off      rawhide whip
Torso    copper chainmail
Float    gem of guarding
```

Potential additional slots may be shown if important and space allows.

Do not expand every empty slot.

If space is constrained, prefer important equipped slots over low-value empty slots.

The full equipment model belongs in the Character/Inventory workspace described later.

---

# 4. Equipment hover inspection

Every equipped item rendered in the right rail must be hoverable.

Hovering an item should display a compact item inspection popover using existing parsed item/Codex knowledge.

Conceptual example:

```text
Copper Sword

Weapon • Sword

Damage       ...
Weight       ...
Material     copper
Wear slot    wielded
Flags        ...
Identified   yes
Observed     ...
```

Use available data only.

Do not fabricate missing stats.

The popover should merge knowledge from:

- current equipment state
- parsed `identify` information
- Codex item knowledge

Do not make the UI query SQLite directly.

Use the existing domain/Codex APIs.

---

# 5. Room section hierarchy

The room section should prioritize current gameplay information.

Do not use a generic `ROOM / CONTEXT` heading.

The room name is the heading:

```text
The Bar in the Adventurer's Lounge
inside • light
```

Then exits immediately:

```text
EXITS
[N] [E] [W]
```

Then visible entities.

Use non-empty categories as the primary content.

Example:

```text
PEOPLE 2
• an aelin rogue
• a tall, outgoing alatharya woman

FIXTURES 1
• a small sign
```

If Objects or Corpses are empty, do not allocate full sections to them.

Either omit them or show a very compact summary:

```text
Objects 0 • Corpses 0
```

The player should see actual entity names, not just counts.

---

# 6. People, MOBs, objects, fixtures

Use actual parsed entities.

Do not collapse important current-world information into counts alone.

Priority should be:

1. hostile / combat-relevant MOBs
2. other people/MOBs
3. interactable fixtures
4. objects
5. corpses

If a domain distinction exists between NPCs, MOBs, players, and general people, preserve it in the data model.

The UI may group them intelligently, but do not lose semantic type information.

---

# 7. Recent observations

Recent observations should remain visible but compact.

Show only a small number of the most recent meaningful observations.

Example:

```text
RECENT

• rogue stretches his wings
• woman polishes the bar
```

Do not repeat static room-description prose unnecessarily.

Prefer dynamic activity.

The section should not grow indefinitely.

Use a small fixed recent-history window.

---

# 8. Room memory

Room memory is useful but secondary.

Render compactly:

```text
MEMORY

11 visits • 4 routes • 6 recurring
Last observed 9/27/2026 9:06 PM
```

Do not use three large statistic cards.

Do not let room memory displace current room information.

---

# 9. Exits and directional scanning

Important behavior correction:

`scan` requires a direction.

Do not render a generic bare `Scan` action that sends:

```text
scan
```

because that is not valid gameplay behavior.

Scanning belongs with directional information.

For each known exit direction, provide access to:

```text
scan north
scan east
scan west
```

Do not overload the normal exit click if exit buttons already mean movement.

Preferred interaction options, in order:

### Option A: exit context menu

Primary click:

```text
[N]
```

performs normal movement/navigation behavior.

Secondary click/context action exposes:

```text
Go North
Scan North
```

### Option B: hover/flyout

Hovering or activating an exit exposes a small secondary scan action.

### Option C: paired compact controls

If required:

```text
N  [scan]
E  [scan]
W  [scan]
```

but avoid unnecessary visual clutter.

Do not add a global Scan button to the bottom action row.

---

# 10. Bottom context actions

The generic right-rail action row should be reduced to actions that make sense without additional input.

Use:

```text
[Look] [Where] [Inv] [Map]
```

Do not include bare `Scan`.

Buttons should be compact shortcut controls, not large slabs.

Use the existing NexMUD icon system.

---

# 11. Character/Inventory workspace

Create a dedicated Character/Inventory workspace accessible from the existing Character navigation.

The World transcript must remain visible according to the existing NexMUD workspace rules.

The Character workspace should feel like a refined ARPG character screen adapted to text-game semantics.

Do not use a fake rendered human avatar.

Use a spatial equipment-slot layout.

---

# 12. Paper-doll equipment model

Build a text-first ARPG-style paper doll using Avendar's actual wear slots.

Conceptual layout:

```text
                  [ HEAD ]

        [ NECK ]             [ FLOATING ]

                  [ TORSO ]

        [ ARMS ]             [ HANDS ]

      [ WRIST 1 ]           [ WRIST 2 ]

       [ RING 1 ]           [ RING 2 ]

               [ ABOUT BODY ]

                  [ WAIST ]

                  [ LEGS ]

                  [ FEET ]

      MAIN HAND             OFF HAND

     copper sword          rawhide whip
```

Support Avendar-specific slots such as:

- head
- neck
- torso
- legs
- feet
- hands
- arms
- about body
- waist
- wrist 1
- wrist 2
- finger/ring 1
- finger/ring 2
- wielded
- dual wielded
- floating nearby

Use the actual domain slot names.

Do not invent a generic RPG slot model if the game provides different semantics.

---

# 13. Paper-doll visual language

The paper doll should communicate spatial equipment placement without pretending NexMUD has graphical armor art.

Use:

- framed slot cells
- clear slot labels
- equipped item name
- small slot icon
- subtle highlighting
- hover states
- selected-item state

Empty slots should be visible but subdued.

Occupied slots should have stronger contrast.

Do not make every slot a huge card.

---

# 14. Item hover behavior

Hovering any equipped item in the paper doll should open the same Codex-backed inspection popover used in the gameplay rail.

This should be a reusable component.

Conceptually:

```text
<ItemInspectionPopover>
```

It consumes a canonical item reference and available knowledge.

Do not duplicate item formatting logic in multiple views.

---

# 15. Character workspace layout

Use a split workspace approximately like:

```text
┌─────────────────────────────┬─────────────────────────────────────┐
│                             │ Randolph the Smuggler               │
│                             │ Human • Thief • Level 6             │
│                             │                                     │
│                             │            [ HEAD ]                 │
│            WORLD            │       [NECK]    [FLOAT]             │
│                             │            [TORSO]                  │
│                             │      ...paper doll...               │
│                             │                                     │
│                             │ Main: copper sword                  │
│                             │ Off:  rawhide whip                  │
│                             │                                     │
│                             │ Combat / attributes                 │
│                             ├─────────────────────────────────────┤
│                             │ INVENTORY                           │
│                             │ Search / filters                    │
│                             │ item list                           │
├─────────────────────────────┴─────────────────────────────────────┤
│ Command input                                                     │
└───────────────────────────────────────────────────────────────────┘
```

The exact split may follow the established main-workspace behavior, but the World must remain visible.

---

# 16. Inventory section

The Character workspace should include inventory.

Show:

- carried items
- capacity
- current weight
- maximum weight
- wealth

Example:

```text
INVENTORY

17 / 100 items
85 / 352 weight
2g 1s 120c

Search...

copper polearm
copper chainmail shirt
quilted bedroll
small hide pack
wooden canoe
copper sword
copper dagger
...
```

Support useful filters where existing domain information permits, such as:

```text
All
Weapons
Armor
Consumables
Containers
Other
```

Do not invent classifications the parser/Codex cannot support reliably.

---

# 17. Item selection

Selecting an item in Inventory should show richer item detail using Codex/identify knowledge.

Potential actions may eventually include:

- wear
- remove
- wield
- dual wield
- examine
- identify
- drop
- put into container

Do not implement unsupported actions in this pass merely because they appear useful.

Only expose actions that map correctly to existing game/domain behavior.

---

# 18. Reuse and architecture

Create reusable UI/domain presentation components.

Conceptually:

```text
CharacterSummary
CompactLoadout
EquipmentSlot
EquipmentPaperDoll
InventoryList
ItemInspectionPopover
RoomIntel
EntityList
ExitControl
RoomMemorySummary
ContextActionBar
```

Do not duplicate item inspection, stat formatting, or room entity formatting.

Use canonical state.

Do not parse transcript text inside views.

---

# 19. Information hierarchy

The right rail should visually prioritize:

```text
1. character identity
2. useful character summary
3. compact equipped loadout
4. room name
5. exits
6. visible entities
7. recent activity
8. room memory
9. actions
```

The Character workspace should prioritize:

```text
1. equipment / paper doll
2. item inspection
3. attributes/combat stats
4. inventory
5. carrying capacity / wealth
```

---

# 20. Density

Reduce the amount of dead vertical space.

Do not achieve this by increasing font size excessively.

Instead:

- tighten section spacing
- collapse empty categories
- avoid repetitive headings
- avoid oversized action buttons
- avoid verbose equipment prose

The rail should feel dense but readable.

---

# 21. Visual consistency

Use the existing approved NexMUD visual system:

- obsidian/midnight backgrounds
- parchment primary text
- brass structural accents
- semantic colors
- current icon family
- existing typography system

Do not generate new decorative assets in this pass.

Do not redesign the application chrome.

Focus on information architecture and component composition.

---

# 22. Acceptance criteria

The right rail pass is complete when:

- character identity is immediately visible
- attributes and combat stats are compact
- equipment remains useful without becoming a prose dump
- equipped items are hoverable
- hover uses Codex/identify data
- actual room entities are immediately visible
- empty categories do not waste space
- exits are immediately visible
- directional scan is available from exit controls
- no invalid bare `scan` button exists
- recent observations remain compact
- room memory is secondary
- bottom action bar contains only valid context-free actions

The Character/Inventory workspace is complete when:

- World remains visible
- Avendar equipment slots are represented spatially
- occupied slots show equipped items
- empty slots remain visible but subdued
- equipped items support the same inspection popover
- inventory is visible and searchable
- capacity, weight, and wealth are visible
- item information uses canonical/Codex state
- no fake graphical armor/avatar is required

Do not modify unrelated areas in this pass.

---