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
    /// <summary>Kills still needed for this target, or 0 if complete.</summary>
    int KillsRemaining(HuntTarget target);
    bool IsComplete(HuntTarget target) => KillsRemaining(target) == 0;
}

/// <summary>Gets the player to a zone and to a point inside it (teleport, mount, fly, walk).</summary>
public interface ITravelService
{
    bool IsBusy { get; }
    bool IsInZone(uint territoryId);
    void TravelToZone(uint territoryId);
    void MoveTo(Vector3 destination);
    void Stop();
}

/// <summary>Produces natural-looking search waypoints inside a target's area.</summary>
public interface ISearchPattern
{
    IEnumerable<Vector3> Waypoints(Vector3 center, float radius);
}

/// <summary>Finds a live instance of the target monster near the player.</summary>
public interface ITargetScanner
{
    IBattleNpc? FindNearest(HuntTarget target);
}

/// <summary>Turns BossMod Reborn / Rotation Solver Reborn on and off and reports fight state.</summary>
public interface ICombatController
{
    bool InCombat { get; }
    void Engage(IBattleNpc mob);
    void Disengage();
}
