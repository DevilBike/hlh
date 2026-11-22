using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace HuntingLogHelper.Core;

// Each module talks to the rest of the plugin only through these interfaces.
// Swap or rewrite one implementation without touching the others.

/// <summary>Static location data: where each hunting log monster lives.</summary>
public interface ITargetDatabase
{
    IReadOnlyList<HuntTarget> All(LogKind kind);
}

/// <summary>Reads the player's hunting log progress from game memory.</summary>
public interface ILogProgressReader
{
    /// <summary>Log index (ClassJob.MonsterNote) for a class/job, or -1 if it has no hunting log.</summary>
    int LogIndexForClassJob(uint classJobId);

    /// <summary>0-based rank the player is currently working on in this log.</summary>
    int CurrentRank(int logIndex);

    /// <summary>Kills still needed for this target, 0 if complete, or -1 if the game data is not ready.</summary>
    int KillsRemaining(HuntTarget target);
}

/// <summary>Gets the player to a zone and to a point inside it (teleport, mount, fly, walk).</summary>
public interface ITravelService
{
    /// <summary>True while teleporting, zoning, mounting or following a path.</summary>
    bool IsBusy { get; }
    bool IsReady { get; }
    /// <summary>True while on a mount (flying or not).</summary>
    bool IsMounted { get; }
    bool IsInZone(uint territoryId);
    void TravelToZone(HuntArea area);
    /// <summary>Path to a point. Mounts/flies for long trips when the config allows it.</summary>
    void MoveTo(Vector3 destination, bool allowMount);
    void Dismount();
    void Stop();
    /// <summary>Per-frame housekeeping (e.g. finish a mount-then-move).</summary>
    void Update();
}

/// <summary>Produces natural-looking search waypoints inside a target's area.</summary>
public interface ISearchPattern
{
    IEnumerable<Waypoint> Waypoints(HuntArea area, Vector3 playerPosition);
}

/// <summary>Finds a live instance of the target monster near the player.</summary>
public interface ITargetScanner
{
    IBattleNpc? FindNearest(HuntTarget target, float maxDistance);
}

/// <summary>Turns BossMod Reborn / Rotation Solver Reborn on and off and reports fight state.</summary>
public interface ICombatController
{
    bool InCombat { get; }
    void Engage(IBattleNpc mob);
    void Disengage();
}
