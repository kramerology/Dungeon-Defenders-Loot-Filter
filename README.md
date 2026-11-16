# DD Loot Filter

A loot filter for Dungeon Defenders 1, inspired by Path of Exile. Set minimum stats for each gear slot. Any dropped item that meets them gets a green dot drawn on it.

## Run

1. Open `dist/DDLootFilter.exe`. The game can start before or after the filter.
2. Run Dungeon Defenders in windowed or borderless mode. Exclusive fullscreen hides the overlay.
3. In the filter window, pick DPS, Builder or Hybrid for each gear group (or switch to **Advanced** to set conditions stat by stat). Then click **Return to game**, which minimizes the window. Closing the window quits the filter.

The window has three parts: a sidebar listing character profiles, the main pane with the open profile in Quick or Advanced mode, and a status bar showing the game connection and how many drops match.

**Show dots in game** (next to the Quick/Advanced switch) is a per-profile setting. Turn it off to stop drawing dots for that character without changing their filter. In the sidebar, each profile has a green dot when its dots are on and a grey dot when they're off.

## Character profiles and split-screen

Every character has its own filter profile. A character is recognised by its hero GUID, so its profile follows it whichever player slot it's in, and survives renames and level-ups.

- The **Profiles** sidebar lists the characters in the game now (badged "Playing", or "P1"/"P2" in split-screen), then other saved characters, then the **Default** template.
- The first time a character is seen, its profile starts as a copy of **Default**. Edit Default to change what new characters start with.
- When the line-up changes (hero swap, a player joins or leaves), the window jumps to Player 1's character, unless the profile you're editing is still in the game.
- The Equipped values show the gear of the character whose profile is open.

In local split-screen, each player's part of the window gets its own dots. They're projected with that player's camera and judged by that character's profile against that character's gear. The status line shows each player's match count. The layout comes from each `LocalPlayer`'s `Origin`/`Size`. A player joining or leaving changes it, and the reader rebuilds.

## Class filter

**Only show drops my class can equip** is on by default, for every profile. The switch appears in both Quick and Advanced mode; it's one setting, so turning it on in one mode turns it on in the other. When on, a drop only gets a dot if the character's class can equip it.

It uses the game's own class test from `DunDefHero.AllowEquipmentPickup`. Items with `AccessoryRequirements` (shields, for example) need one of the hero's `AccessoryTypes`. Other items pass if their `weaponType` is 0 (armor, familiars and most accessories), matches the hero's `HeroWeaponType` or is in `HeroWeaponTypes`. Items with `bCanBeEquipped` off never pass. The level requirement isn't checked; use the tier range for that. If the hero can't be read, nothing is hidden because of class. In split-screen each player's dots use their own class.

## Quick mode

The filter window opens in **Quick** mode. Pick a role for each gear group: Weapons, Armor (helmet, torso, gloves, boots), Familiars and Accessories (brooch/hat, bracers, shield, mask). **Set all to** sets every group at once. The mode selected at the top of the pane is the one running in game.

A drop gets a dot when the role's stats add up to more than the same stats on the item your hero has equipped in that slot:

| Role | Stats summed |
| --- | --- |
| DPS | Hero Health, Hero Damage, Hero Casting Rate, Ability 1, Ability 2 (+ tooltip weapon damage on weapons) |
| Builder | Defense Health, Defense Attack Rate, Defense Damage, Defense Range (AoE) |
| Hybrid | Everything DPS and Builder count |

Hero Speed isn't counted because it caps quickly. The equipped totals show under each group.

**Min tier / Max tier** in Quick mode limit every group to a quality range, for example Godly (lvl 60) to Ultimate++ (lvl 100). Fine-tune in Advanced carries the range over to each slot.

**Fine-tune in Advanced…** copies the Quick choices into the Advanced per-slot rules as custom sums, so you can add or remove stats. It only replaces slots whose group isn't Off. Whichever mode is selected is the one in use. Filters saved before Quick mode existed open in Advanced.

## How matching works (Advanced mode)

- Each slot has its own rule: Weapon, Helmet, Torso, Gloves, Boots, Familiar, Brooch/Hat, Bracers, Shield and Mask. Pick a slot in the list on the left; the dot shows whether it's on and the badge counts its conditions. **Highlight drops** turns the slot on or off.
- Each stat has a condition: **Ignore**, **At least** (enter a whole number, may be negative; an invalid entry gets a red border and is ignored), or **Better than equipped**.
- **Better than equipped** passes when the drop's value is strictly higher than the same stat on the item your current hero has equipped in that slot. The **Equipped** column shows those values live. If the slot is empty, any drop passes. If the hero can't be read, the condition fails. When a slot holds two items (dual wielding), the weaker value of each stat is used. Equipped values include upgrades you've already put into the item; drops are un-upgraded.
- **Min tier / Max tier** limit the quality range. Tiers run from lowest to highest: Cursed, Torn, Worn, Stocky, Solid, Sturdy, Polished, Shining, Powerful, Amazing, Epic, Legendary, Godly, Mythical, Transcendent, Supreme, Ultimate, Ultimate 93, Ultimate+, Ultimate++. Each tier lists the level needed to equip it, for example Godly 60, Mythical 74, Ultimate 90 and Ultimate++ 100. These come from the game's standard `EquipLevelRequirements` table; a few familiars and special items use their own. The tier range always applies, whichever stat mode is chosen.
- **Custom sum**: tick stats in the **Sum** column to add them together, then give the **Custom sum** row at the bottom of the tab its own condition. For example, tick Defense Damage and Defense Attack Rate on Gloves and pick **Better than equipped**. If your gloves have 10 + 5, a drop passes when those two stats add up to more than 15. The Equipped column shows the equipped total. The built-in totals and max level can't be ticked. The custom sum counts as one condition alongside the per-stat ones.
- **Match: All conditions**: the item must pass every stat condition you set. **Any condition**: passing one of them is enough.
- If a slot is on and every stat is ignored, every drop of that slot is highlighted. Slots that are off are never highlighted.
- Every slot has the ten hero and defense stats, three sums (hero 1–6, defense 7–10, all ten) and the max upgrade level. Armor slots also have the four resistances and their sum. Weapons also have their weapon stats: damage, alt damage, elemental damage, projectiles and so on.
- The filter saves automatically to `%APPDATA%\DDLootFilter\filter.json`.

Values are meant to match the item tooltip:

- **Hero/defense stats and resistances** are stored on the item exactly as the tooltip shows them.
- **Weapon damage, alt damage and elemental damage** are converted with the game's own tooltip formulas, taken from `HeroEquipment`'s display functions. Those formulas combine the weapon template's base damage, the player damage multiplier and the melee swing-speed factor. For example, a pike with stored rolls 81 / 49 / 6 shows as 151 / 47 / 3, and the filter uses 151 / 47 / 3.
- **Clip ammo, shots per second and blocking** include the template's base value, like the tooltip.
- Armor set bonuses aren't included. Neither are later upgrades.

## How it reads the game

It uses the same read-only approach as DungeonRoutes. The game is opened with query and read rights only. Nothing is written, injected or modified. It only supports the game build with SHA-256 `d1c4ad5d…badc`. Other builds are rejected.

Every 200 ms it scans the object table for `DunDefDroppedEquipment` actors in the current level. It skips hidden, deleted and item-box-transferred ones. For each item it reads `MyEquipmentObject`, which is a `HeroEquipment`. From that it decodes:

- `StatModifiers[11]`, indexed by the game's `LevelUpValueType` enum (1 = Hero Health … 10 = Defense AoE)
- `DamageReductions[4]` (damage type + percent)
- the `Weapon*Bonus` fields
- `EquipmentType` and `MaxEquipmentLevel`

For **Better than equipped**, the same decoding runs on each local player's gear: `DunDefPlayerController.myHero` → `DunDefHero.HeroEquipments`. Local players are the controllers whose `Player` is a `LocalPlayer`. The character key is `DunDefHero.GUID1–4`, and the label is `HeroName`, `HeroClassDisplayName` and `HeroLevel`.

Reflected field offsets are looked up at runtime. A scan takes about 5 ms. The full object table is rebuilt every 20 s and whenever the level changes. That rebuild takes about 0.7 s and runs in the background.

Dots are projected from each item's world location using the live camera. They draw only while the game is the foreground window. They are not hidden behind walls.

## Build / diagnose

```powershell
dotnet build -c Release
dotnet bin/Release/net10.0-windows/DDLootFilter.dll --diagnose            # drops.json: every drop, decoded stats, filter result, timings
dotnet bin/Release/net10.0-windows/DDLootFilter.dll --diagnose --render   # also drops-overlay.png with a dot on every drop
dotnet bin/Release/net10.0-windows/DDLootFilter.dll --render-ui           # filter-ui.png of the filter window (--advanced for that view, --size WxH)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

The window's look (colours, fonts and control styles) lives in `Theme.xaml`; `FilterWindow.cs` builds the layout from it.

`tools/probe.py` is the Python probe used to find the item classes, fields and enums. Errors are logged to `%LOCALAPPDATA%\DDLootFilter\overlay.log`.
