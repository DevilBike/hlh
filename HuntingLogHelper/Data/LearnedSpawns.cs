using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using HuntingLogHelper.Core;

namespace HuntingLogHelper.Data;

/// <summary>
/// Spawn points the plugin has seen with its own eyes, saved in the plugin config folder.
/// Fills the gaps for targets where only an approximate sub-area is known.
/// </summary>
public sealed class LearnedSpawns
{
    private const int MaxPerTarget = 40;
    private const float Dedup = 6f;

    private readonly string path;
    // key: "bnpcNameId:territoryId" -> points (x, y, z)
    private readonly Dictionary<string, List<float[]>> points;
    private DateTime lastSave = DateTime.MinValue;
    private bool dirty;

    public LearnedSpawns(string configDirectory)
    {
        path = Path.Combine(configDirectory, "learned_spawns.json");
        try
        {
            points = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, List<float[]>>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not read learned spawns, starting fresh.");
            points = new();
        }
    }

    private static string Key(uint nameId, uint territory) => $"{nameId}:{territory}";

    public void Record(uint nameId, uint territory, Vector3 pos)
    {
        var key = Key(nameId, territory);
        if (!points.TryGetValue(key, out var list)) points[key] = list = new();
        if (list.Count >= MaxPerTarget) return;
        if (list.Any(p => Vector2.Distance(new(p[0], p[2]), new(pos.X, pos.Z)) < Dedup)) return;
        list.Add(new[] { pos.X, pos.Y, pos.Z });
        dirty = true;
    }

    public IReadOnlyList<float[]> For(uint nameId, uint territory) =>
        points.TryGetValue(Key(nameId, territory), out var list) ? list : Array.Empty<float[]>();

    /// <summary>An area built from what we've seen, or null if we haven't seen enough yet.</summary>
    public HuntArea? AreaFor(HuntTarget target, HuntArea baseArea)
    {
        var seen = For(target.BNpcNameId, baseArea.TerritoryId);
        if (seen.Count < 3) return null;
        var cx = seen.Average(p => p[0]);
        var cz = seen.Average(p => p[2]);
        return new HuntArea
        {
            TerritoryId = baseArea.TerritoryId,
            AetheryteId = baseArea.AetheryteId,
            Center = new[] { cx, cz },
            Radius = Math.Max(20f, seen.Max(p => Vector2.Distance(new(p[0], p[2]), new(cx, cz)))),
            SpawnPoints = seen.Select(p => new[] { p[0], p[2] }).ToList(),
            Observations = seen.Count,
        };
    }

    public void SaveIfDirty()
    {
        if (!dirty || DateTime.UtcNow - lastSave < TimeSpan.FromSeconds(30)) return;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(points));
            dirty = false;
            lastSave = DateTime.UtcNow;
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Could not save learned spawns."); }
    }
}
