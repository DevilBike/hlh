"""
Builds HuntingLogHelper/Data/class_targets.json and gc_targets.json.

Inputs (downloaded by tools/fetch_data.sh into tools/.cache):
  - Game sheets from xivapi/ffxiv-datamining (csv/en): MonsterNote, MonsterNoteTarget,
    BNpcName, PlaceName, Map, ClassJob
  - Observed spawn positions and aetherytes from ffxiv-teamcraft (libs/data/src/lib/json)

Instead of one big circle per target, each target gets the real observed spawn points
(world X/Z) in the zones the hunting log names, so the plugin can wander between them.
"""
import csv, json, math, os, statistics, sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, ".cache")
OUT = os.path.join(HERE, "..", "HuntingLogHelper", "Data")

MAX_SPAWNS = 40          # spawn points kept per target zone
DEDUP_WORLD = 6.0        # yalms; merge spawn points closer than this
CLUSTER_RADIUS = 60.0    # yalms; points within this of the densest point form the hunt area


def sheet(name):
    with open(os.path.join(CACHE, f"{name}.csv"), encoding="utf-8-sig") as f:
        rows = list(csv.reader(f))
    header = rows[0]
    out = {}
    for r in rows[1:]:
        if not r or not r[0].lstrip("-").isdigit():
            continue
        out[int(r[0])] = dict(zip(header, r))
    return out


def sheet_subrows(name):
    """Sheets with subrows (id like '12.3') -> {row: [dict, ...]}"""
    with open(os.path.join(CACHE, f"{name}.csv"), encoding="utf-8-sig") as f:
        rows = list(csv.reader(f))
    header = rows[0]
    out = defaultdict(list)
    for r in rows[1:]:
        if not r or "." not in r[0]:
            continue
        out[int(r[0].split(".")[0])].append(dict(zip(header, r)))
    return out


def to_world(map_coord, size_factor, offset):
    c = size_factor / 100.0
    return (map_coord - 1.0 - 20.5 / c) * 2048.0 / 41.0 - offset


def main():
    notes = sheet("MonsterNote")
    note_targets = sheet("MonsterNoteTarget")
    bnpc = sheet("BNpcName")
    place = sheet("PlaceName")
    maps = sheet("Map")
    classjob = sheet("ClassJob")
    markers = sheet_subrows("MapMarker")
    territories = sheet("TerritoryType")
    monsters = json.load(open(os.path.join(CACHE, "monsters.json"), encoding="utf-8"))
    aetherytes = json.load(open(os.path.join(CACHE, "aetherytes.json"), encoding="utf-8"))

    # MonsterNote row id -> log index. Classes: ClassJob id * 10000 + entry. GC: 1000000 * gc + entry.
    class_log_index = {}
    for cid, row in classjob.items():
        idx = int(row["MonsterNote"] or -1)
        if 0 <= idx < 127 and cid < 19:          # base classes only; jobs share their parent's log
            class_log_index[cid] = (idx, row["NameEnglish"])

    aeth_by_territory = defaultdict(list)
    for a in aetherytes:
        if a.get("type") == 0 and a.get("territory"):
            aeth_by_territory[a["territory"]].append(a)

    def map_info(map_id):
        m = maps[map_id]
        return int(m["SizeFactor"]), int(m["OffsetX"]), int(m["OffsetY"]), int(m["TerritoryType"])

    def nearest_aetheryte(territory, x, z):
        best = None
        for a in aeth_by_territory.get(territory, []):
            sf, ox, oy, _ = map_info(a["map"])
            ax, az = to_world(a["x"], sf, ox), to_world(a["y"], sf, oy)
            d = math.hypot(ax - x, az - z)
            if best is None or d < best[0]:
                best = (d, a["id"])
        return best[1] if best else 0

    def zone_targets(bnpc_id, zone_ids):
        """Group observed spawns by territory, keep the densest cluster in each."""
        positions = monsters.get(str(bnpc_id), {}).get("positions", [])
        by_terr = defaultdict(list)
        levels = []
        for p in positions:
            if p.get("fate"):
                continue
            if zone_ids and p["zoneid"] not in zone_ids:
                continue
            if p["map"] not in maps:
                continue
            sf, ox, oy, terr = map_info(p["map"])
            by_terr[terr].append((to_world(p["x"], sf, ox), to_world(p["y"], sf, oy)))
            levels.append(p["level"])
        areas = []
        for terr, pts in by_terr.items():
            # densest point = the one with most neighbours inside CLUSTER_RADIUS
            def density(a):
                return sum(1 for b in pts if math.hypot(a[0] - b[0], a[1] - b[1]) <= CLUSTER_RADIUS)
            core = max(pts, key=density)
            cluster = [p for p in pts if math.hypot(p[0] - core[0], p[1] - core[1]) <= CLUSTER_RADIUS]
            kept = []
            for p in cluster:
                if all(math.hypot(p[0] - k[0], p[1] - k[1]) > DEDUP_WORLD for k in kept):
                    kept.append(p)
            kept = kept[:MAX_SPAWNS]
            cx = statistics.fmean(p[0] for p in kept)
            cz = statistics.fmean(p[1] for p in kept)
            radius = max([math.hypot(p[0] - cx, p[1] - cz) for p in kept] + [15.0])
            areas.append({
                "TerritoryId": terr,
                "AetheryteId": nearest_aetheryte(terr, cx, cz),
                "Center": [round(cx, 1), round(cz, 1)],
                "Radius": round(radius, 1),
                "SpawnPoints": [[round(x, 1), round(z, 1)] for x, z in kept],
                "Observations": len(pts),
            })
        areas.sort(key=lambda a: -a["Observations"])
        level = int(statistics.median(levels)) if levels else 0
        return areas, level

    def is_overworld(terr):
        t = territories.get(terr)
        return t is not None and t.get("TerritoryIntendedUse") == "1"

    def marker_areas(zone_ids, location_ids):
        """Fallback when no spawns were observed: the map label of the named sub-area."""
        areas = []
        for map_id, m in maps.items():
            if int(m["PlaceName"] or 0) not in zone_ids:
                continue
            sf, ox, oy, terr = int(m["SizeFactor"]), int(m["OffsetX"]), int(m["OffsetY"]), int(m["TerritoryType"])
            c = sf / 100.0
            for mk in markers.get(int(m["MapMarkerRange"] or 0), []):
                if int(mk["PlaceNameSubtext"] or 0) not in location_ids:
                    continue
                x = (int(mk["X"]) - 1024) / c - ox
                z = (int(mk["Y"]) - 1024) / c - oy
                areas.append({
                    "TerritoryId": terr,
                    "AetheryteId": nearest_aetheryte(terr, x, z),
                    "Center": [round(x, 1), round(z, 1)],
                    "Radius": 70.0,
                    "SpawnPoints": [],
                    "Observations": 0,
                })
        return areas

    class_out, gc_out, missing, approx = [], [], [], []
    for nid, row in sorted(notes.items()):
        if not row.get("Name"):
            continue
        if nid >= 1000000:
            kind, owner, entry = "GrandCompany", nid // 1000000, nid % 1000000
            log_index = 7 + owner      # Maelstrom 8, Twin Adder 9, Immortal Flames 10
            owner_name = row["Name"].rsplit(" ", 1)[0]
        else:
            cid, entry = nid // 10000, nid % 10000
            if cid not in class_log_index:
                continue
            kind, owner = "Class", cid
            log_index, owner_name = class_log_index[cid]
        rank = (entry - 1) // 10
        slot_in_rank = (entry - 1) % 10
        for i in range(4):
            tid = int(row[f"MonsterNoteTarget[{i}]"])
            count = int(row[f"Count[{i}]"])
            if tid == 0 or count == 0:
                continue
            t = note_targets[tid]
            bid = int(t["BNpcName"])
            zones = {int(t[f"PlaceNameZone[{z}]"]) for z in range(3)} - {0}
            locations = {int(t[f"PlaceNameLocation[{z}]"]) for z in range(3)} - {0}
            areas, level = zone_targets(bid, zones)
            approximate = False
            if not areas:
                areas = marker_areas(zones, locations)
                approximate = bool(areas)
            areas = [a for a in areas if is_overworld(a["TerritoryId"])]
            in_duty = not areas
            target = {
                "Kind": kind,
                "Owner": owner,
                "OwnerName": owner_name,
                "LogIndex": log_index,
                "MonsterNoteId": nid,
                "Rank": rank,
                "Entry": slot_in_rank,
                "Slot": i,
                "BNpcNameId": bid,
                "Name": bnpc[bid]["Singular"],
                # Observed median level, else the hunting log's own pacing (rank 1 = Lv1-10, etc.).
                "Level": level or (rank * 10 + slot_in_rank + 1),
                "RequiredKills": count,
                "ZoneNames": [place[z]["Name"] for z in sorted(zones) if z in place],
                "InDuty": in_duty,
                "Approximate": approximate,
                "Areas": areas,
            }
            if approximate:
                approx.append(f"{owner_name} rank {rank+1}: {target['Name']}")
            if in_duty:
                missing.append(f"{owner_name} rank {rank+1}: {target['Name']}")
            (class_out if kind == "Class" else gc_out).append(target)

    os.makedirs(OUT, exist_ok=True)
    for name, data in (("class_targets.json", class_out), ("gc_targets.json", gc_out)):
        with open(os.path.join(OUT, name), "w", encoding="utf-8") as f:
            json.dump(data, f, indent=1, ensure_ascii=False)
    print(f"class targets: {len(class_out)}, gc targets: {len(gc_out)}, "
          f"sub-area estimate only: {len(approx)}, no open-world location (skipped): {len(missing)}")
    for m in missing[:20]:
        print("  ", m)


if __name__ == "__main__":
    sys.exit(main())
