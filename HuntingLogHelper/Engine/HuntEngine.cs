using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using HuntingLogHelper.Core;

namespace HuntingLogHelper.Engine;

public enum HuntState { Idle, Choosing, Traveling, Searching, Approaching, Fighting, Verifying, Done, Stopped }

/// <summary>
/// The hunt loop. Only knows the Core interfaces, so each module can change independently.
/// Choose next target, travel, search, fight, verify the log entry in-game, repeat.
/// </summary>
public sealed class HuntEngine
{
    private const float ScanRange = 45f;
    private const float EngageRange = 3.5f;
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StuckWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan VerifyDelay = TimeSpan.FromSeconds(2.5);
    private const int MaxSearchRounds = 4;

    private readonly ITargetDatabase db;
    private readonly ILogProgressReader progress;
    private readonly ITravelService travel;
    private readonly ISearchPattern search;
    private readonly ITargetScanner scanner;
    private readonly ICombatController combat;
    private readonly Configuration config;
    private readonly Random rng = new();

    private readonly HashSet<HuntTarget> skipped = new();
    private HuntArea? area;
    private Queue<Waypoint> waypoints = new();
    private IBattleNpc? mob;
    private int searchRounds;
    private int travelAttempts;
    private DateTime stateSince = DateTime.UtcNow;
    private DateTime waitUntil = DateTime.MinValue;
    private Vector3 lastPos;
    private DateTime lastMoved = DateTime.UtcNow;
    private int stuckCount;

    public HuntState State { get; private set; } = HuntState.Idle;
    public QueuedTarget? Current { get; private set; }
    public string StatusText { get; private set; } = "Idle";
    public bool Running => State is not (HuntState.Idle or HuntState.Done or HuntState.Stopped);

    public HuntEngine(ITargetDatabase db, ILogProgressReader progress, ITravelService travel,
        ISearchPattern search, ITargetScanner scanner, ICombatController combat, Configuration config)
    {
        this.db = db; this.progress = progress; this.travel = travel;
        this.search = search; this.scanner = scanner; this.combat = combat; this.config = config;
    }

    /// <summary>Incomplete open-world targets for the current class's log, closest to the player's level first.</summary>
    public List<QueuedTarget> BuildQueue()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return new();
        var logIndex = progress.LogIndexForClassJob(me.ClassJob.RowId);
        if (logIndex < 0) return new();
        int level = me.Level;

        return db.All(LogKind.Class)
            .Where(t => t.LogIndex == logIndex && !t.InDuty && t.Areas.Count > 0 && !skipped.Contains(t))
            .Where(t => t.Level <= level + config.MaxLevelsAbove)
            .Select(t => new QueuedTarget(t, progress.KillsRemaining(t)))
            .Where(q => q.KillsRemaining > 0)
            .OrderBy(q => Math.Abs(q.Target.Level - level))
            .ThenBy(q => q.Target.Entry)
            .ToList();
    }

    public void Start()
    {
        skipped.Clear();
        stuckCount = 0;
        Go(HuntState.Choosing, "Starting");
    }

    public void Stop(string reason = "Stopped")
    {
        travel.Stop();
        combat.Disengage();
        mob = null;
        Go(HuntState.Stopped, reason);
    }

    /// <summary>Called every framework tick.</summary>
    public void Tick()
    {
        if (!Running) return;
        travel.Update();

        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return;                       // loading screen
        if (me.IsDead || me.CurrentHp == 0) { Stop("Stopped: you died"); return; }
        if (Plugin.Condition[ConditionFlag.BoundByDuty]) { Stop("Stopped: duty started"); return; }
        if (DateTime.UtcNow < waitUntil) return;

        switch (State)
        {
            case HuntState.Choosing: Choose(me.Position); break;
            case HuntState.Traveling: Travel(me.Position); break;
            case HuntState.Searching: Search(me.Position); break;
            case HuntState.Approaching: Approach(me.Position); break;
            case HuntState.Fighting: Fight(); break;
            case HuntState.Verifying: Verify(); break;
        }
    }

    private void Choose(Vector3 pos)
    {
        var queue = BuildQueue();
        if (queue.Count == 0)
        {
            combat.Disengage();
            Go(HuntState.Done, skipped.Count > 0
                ? $"Done for now ({skipped.Count} target(s) not found, try again later)"
                : "Done: nothing left at your level in the current rank");
            return;
        }

        Current = queue[0];
        var t = Current.Target;
        // Prefer the zone we are in, then zones whose aetheryte is attuned, then the best-observed one.
        var attuned = Plugin.AetheryteList.Select(a => a.AetheryteId).ToHashSet();
        area = t.Areas.FirstOrDefault(a => travel.IsInZone(a.TerritoryId))
               ?? t.Areas.FirstOrDefault(a => attuned.Contains(a.AetheryteId))
               ?? t.Areas[0];
        searchRounds = 0;
        travelAttempts = 0;
        waypoints.Clear();
        Go(HuntState.Traveling, $"Heading to {t.Name} ({Current.KillsRemaining} left)");
    }

    private void Travel(Vector3 pos)
    {
        if (area == null) { Go(HuntState.Choosing, "Choosing"); return; }

        if (!travel.IsInZone(area.TerritoryId))
        {
            if (travel.IsBusy) { stateSince = DateTime.UtcNow; return; }
            if (travelAttempts > 0 && DateTime.UtcNow - stateSince < TravelTimeout) return;
            if (travelAttempts >= 3) { Skip("could not teleport"); return; }
            travelAttempts++;
            travel.TravelToZone(area);
            stateSince = DateTime.UtcNow;
            WaitRandom(1.0, 2.0);
            return;
        }

        if (!travel.IsReady || travel.IsBusy) return; // mesh still loading, or still zoning

        var center = new Vector3(area.Center[0], pos.Y, area.Center[1]);
        if (Flat(pos, center) > area.Radius + 10f)
        {
            travel.MoveTo(center, allowMount: true);
            ResetStuck(pos);
            Go(HuntState.Searching, $"Travelling to {Current!.Target.Name}'s area");
            return;
        }
        Go(HuntState.Searching, $"Searching for {Current!.Target.Name}");
    }

    private void Search(Vector3 pos)
    {
        var t = Current!.Target;
        var found = scanner.FindNearest(t, ScanRange);
        if (found != null)
        {
            travel.Stop();
            mob = found;
            Go(HuntState.Approaching, $"Found {t.Name}");
            WaitRandom(0.2, 0.8); // short human-ish reaction time
            return;
        }

        if (travel.IsBusy)
        {
            if (CheckStuck(pos)) return;
            return;
        }

        if (waypoints.Count == 0)
        {
            if (++searchRounds > MaxSearchRounds) { Skip("not found"); return; }
            waypoints = new Queue<Waypoint>(search.Waypoints(area!, pos));
            if (waypoints.Count == 0) { Skip("no search points"); return; }
        }

        var next = waypoints.Dequeue();
        travel.MoveTo(next.Position, allowMount: Flat(pos, next.Position) > 45f);
        ResetStuck(pos);
        StatusText = $"Searching for {t.Name} (round {searchRounds}/{MaxSearchRounds})";
        if (next.PauseSeconds > 0) waitUntil = DateTime.UtcNow.AddSeconds(next.PauseSeconds);
    }

    private void Approach(Vector3 pos)
    {
        if (mob == null || mob.IsDead || !mob.IsValid())
        {
            Go(HuntState.Searching, $"Lost {Current!.Target.Name}, searching");
            return;
        }

        if (combat.InCombat)
        {
            combat.Engage(mob);
            Go(HuntState.Fighting, $"Fighting {mob.Name}");
            return;
        }

        var dist = Vector3.Distance(pos, mob.Position) - mob.HitboxRadius;
        if (dist > EngageRange)
        {
            if (!travel.IsBusy) travel.MoveTo(mob.Position, allowMount: dist > 60f);
            if (DateTime.UtcNow - stateSince > TimeSpan.FromSeconds(30)) Go(HuntState.Searching, "Could not reach mob, searching");
            return;
        }

        travel.Stop();
        travel.Dismount();
        combat.Engage(mob);
        Go(HuntState.Fighting, $"Fighting {mob.Name}");
    }

    private void Fight()
    {
        if (combat.InCombat)
        {
            // Keep the hunt target selected; BMR/RSR do the rest.
            if (mob != null && !mob.IsDead && Plugin.TargetManager.Target == null) Plugin.TargetManager.Target = mob;
            return;
        }
        if (DateTime.UtcNow - stateSince < TimeSpan.FromSeconds(3)) return; // combat may not have registered yet
        mob = null;
        Go(HuntState.Verifying, "Checking hunting log");
        waitUntil = DateTime.UtcNow + VerifyDelay;
    }

    private void Verify()
    {
        var t = Current!.Target;
        var left = progress.KillsRemaining(t);
        if (left < 0) return; // log data not ready, try next tick
        if (left == 0)
        {
            Plugin.Chat.Print($"[HLH] Hunting log: {t.Name} complete.");
            Go(HuntState.Choosing, $"{t.Name} complete");
            WaitRandom(1.0, 3.0);
            return;
        }
        Current = Current with { KillsRemaining = left };
        Go(HuntState.Searching, $"{t.Name}: {left} left");
        WaitRandom(0.5, 2.0);
    }

    private void Skip(string why)
    {
        var t = Current!.Target;
        skipped.Add(t);
        Plugin.Chat.Print($"[HLH] Skipping {t.Name}: {why}.");
        travel.Stop();
        Go(HuntState.Choosing, $"Skipped {t.Name} ({why})");
    }

    private bool CheckStuck(Vector3 pos)
    {
        if (Vector3.Distance(pos, lastPos) > 1.5f) { ResetStuck(pos); return false; }
        if (DateTime.UtcNow - lastMoved < StuckWindow) return false;
        travel.Stop();
        ResetStuck(pos);
        if (++stuckCount >= 5) { Stop("Stopped: stuck repeatedly"); return true; }
        return true; // next tick picks the next waypoint
    }

    private void ResetStuck(Vector3 pos) { lastPos = pos; lastMoved = DateTime.UtcNow; }

    private void Go(HuntState s, string status)
    {
        State = s;
        StatusText = status;
        stateSince = DateTime.UtcNow;
        Plugin.Log.Debug($"[HLH] {s}: {status}");
    }

    private void WaitRandom(double minSec, double maxSec) =>
        waitUntil = DateTime.UtcNow.AddSeconds(minSec + rng.NextDouble() * (maxSec - minSec));

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
}
