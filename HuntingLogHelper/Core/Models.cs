using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;

namespace HuntingLogHelper.Core;

/// <summary>Which log a target belongs to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LogKind { Class, GrandCompany }

/// <summary>One place a target can be hunted: a zone plus observed spawn points (world X/Z).</summary>
public sealed class HuntArea
{
    public uint TerritoryId { get; set; }
    public uint AetheryteId { get; set; }
    public float[] Center { get; set; } = new float[2];
    public float Radius { get; set; }
    public List<float[]> SpawnPoints { get; set; } = new();
    public int Observations { get; set; }

    [JsonIgnore] public Vector2 CenterXZ => new(Center[0], Center[1]);
}

/// <summary>One monster in one hunting log entry. Built by tools/build_targets.py.</summary>
public sealed class HuntTarget
{
    public LogKind Kind { get; set; }
    public uint Owner { get; set; }
    public string OwnerName { get; set; } = "";
    /// <summary>Index into MonsterNoteManager.RankData (ClassJob.MonsterNote).</summary>
    public int LogIndex { get; set; }
    public uint MonsterNoteId { get; set; }
    /// <summary>0-based rank (0 = rank 1).</summary>
    public int Rank { get; set; }
    /// <summary>0-9 entry inside the rank.</summary>
    public int Entry { get; set; }
    /// <summary>0-3 monster slot inside the entry.</summary>
    public int Slot { get; set; }
    public uint BNpcNameId { get; set; }
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int RequiredKills { get; set; }
    public List<string> ZoneNames { get; set; } = new();
    public bool InDuty { get; set; }
    /// <summary>True when only the sub-area label is known, not observed spawns.</summary>
    public bool Approximate { get; set; }
    public List<HuntArea> Areas { get; set; } = new();

    public override string ToString() => $"{Name} ({OwnerName} rank {Rank + 1})";
}

/// <summary>A target plus how many kills the game says are still needed.</summary>
public sealed record QueuedTarget(HuntTarget Target, int KillsRemaining);

/// <summary>A point to walk to, and how long to idle there before moving on.</summary>
public readonly record struct Waypoint(Vector3 Position, float PauseSeconds);
