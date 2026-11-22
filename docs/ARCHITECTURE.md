# Architecture

The plugin is split into modules. Each one does one job and talks to the others only through the interfaces in `Core/Interfaces.cs`. You can rewrite one module without touching the rest, as long as it still implements its interface.

| Module | Folder | Interface | Job |
|---|---|---|---|
| Models | `Core/` | n/a | Shared records (`HuntTarget`, `QueuedTarget`) |
| Target data | `Data/` | `ITargetDatabase` | Loads where each monster lives from `Data/*.json` |
| Log progress | `Progress/` | `ILogProgressReader` | Reads kills remaining from the in-game hunting log |
| Travel | `Travel/` | `ITravelService` | Lifestream/teleport between zones, vnavmesh with mount and flying inside a zone |
| Search | `Search/` | `ISearchPattern`, `ITargetScanner` | Randomized wander inside the area, and spotting the mob |
| Combat | `Combat/` | `ICombatController` | Turns BossMod Reborn AI and Rotation Solver Reborn on and off |
| Engine | `Engine/` | n/a | The loop: choose, travel, search, fight, verify, repeat |
| UI | `Windows/` | n/a | Status and settings window (`/huntlog`) |

`Plugin.cs` is the only file that picks which implementation backs each interface. To try a different search pattern, write a new `ISearchPattern` class and change one line there.

## Hunt loop

1. **Choose:** build the queue of incomplete class log targets, skipping dungeon targets and anything too far above your level, closest to your level first.
2. **Travel:** go to the target's zone, then mount or fly to the area center.
3. **Search:** walk randomized waypoints inside the area until the scanner sees the mob.
4. **Fight:** target it and let BMR and RSR handle combat.
5. **Verify:** re-read the in-game log. Only move to the next target once the game shows this entry is complete.

## Target data

`HuntingLogHelper/Data/class_targets.json` and `gc_targets.json` are generated, not hand-written:

```
bash tools/fetch_data.sh
python3 tools/build_targets.py
```

The script joins the game's hunting log sheets (MonsterNote, MonsterNoteTarget, BNpcName, PlaceName, Map) from xivapi/ffxiv-datamining with crowd-sourced spawn positions and aetherytes from ffxiv-teamcraft. Instead of one large circle, each target gets the actual spawn points seen in the zone the log names, clustered to the densest spot. When no spawns were recorded, it falls back to the map label of the sub-area the log names (`"Approximate": true`), and the search does a meandering walk there. Targets only found in dungeons are marked `InDuty` and skipped.

## Natural movement

- Search order: drifts between the 3 closest unvisited spawn points, weighted toward the nearest, with 3-7 yalm jitter, so no two runs follow the same path.
- Random short pauses at about a third of waypoints, small random delays before engaging and after kills.
- Mounts for trips over about 45 yalms and flies when the zone's aether currents are complete.

## Hunting log progress

The game only stores kill counts for the rank in progress (`MonsterNoteManager.RankData[logIndex]`). Earlier ranks count as complete, later ranks as locked. The engine re-reads the log after each fight and only moves on once the game says the entry is complete. The window's "Debug: raw hunting log" section shows what the plugin reads, to check against the in-game log.

Grand Company log mode is added after the class log mode is confirmed working in-game.
