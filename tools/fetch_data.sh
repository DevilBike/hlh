#!/usr/bin/env bash
# Downloads the source data for build_targets.py into tools/.cache
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p .cache
DM=https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/en
TC=https://raw.githubusercontent.com/ffxiv-teamcraft/ffxiv-teamcraft/staging/libs/data/src/lib/json
for s in MonsterNote MonsterNoteTarget BNpcName PlaceName Map ClassJob MapMarker TerritoryType; do
  curl -fsSL "$DM/$s.csv" -o ".cache/$s.csv"
done
curl -fsSL "$TC/monsters.json" -o .cache/monsters.json
curl -fsSL "$TC/aetherytes.json" -o .cache/aetherytes.json
echo "Fetched into tools/.cache"
