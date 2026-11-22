using System.Numerics;

namespace HuntingLogHelper.Core;

/// <summary>Which log a target belongs to.</summary>
public enum LogKind { Class, GrandCompany }

/// <summary>One monster entry from a hunting log, plus where to look for it.</summary>
public sealed record HuntTarget(
    LogKind Kind,
    uint ClassJobOrGcId,
    int Rank,
    uint BNpcNameId,
    string Name,
    int Level,
    int RequiredKills,
    uint TerritoryId,
    Vector3 AreaCenter,
    float AreaRadius,
    bool InDuty);

/// <summary>A target plus how many kills the game says are still needed.</summary>
public sealed record QueuedTarget(HuntTarget Target, int KillsRemaining);
