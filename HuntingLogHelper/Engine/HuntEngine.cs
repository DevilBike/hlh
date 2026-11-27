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
    private const float ScanRange = 60f;
    private const float EngageRange = 3.5f;
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StuckWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(3);
    private const int MaxSearchRounds = 4;

    private readonly ITargetDatabase db;
    private readonly ILogProgressReader progress;
    private readonly ITravelService travel;
    private readonly ISearchPattern search;
    private readonly ITargetScanner scanner;
    private readonly ICombatController combat;
    private readonly Configuration config;
    private readonly HuntingLogHelper.Data.LearnedSpawns learned;
    private readonly Random rng = new();
    private DateTime lastLearn = DateTime.MinValue;

    private readonly HashSet<HuntTarget> skipped = new();
    private HuntArea? area;
    private Queue<Waypoint> waypoints = new();
    private IBattleNpc? mob;
    private int searchRounds;
    private bool retriedSkipped;
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
        ISearchPattern search, ITargetScanner scanner, ICombatController combat, Configuration config,
        HuntingLogHelper.Data.LearnedSpawns learned)
    {
        this.learned = learned;
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
            .OrderBy(q => q.Target.Level)
            .ThenBy(q => q.Target.Areas.Any(a => travel.IsInZone(a.TerritoryId)) ? 0 : 1)
            .ThenBy(q => q.Target.Entry)
            .ToList();
    }

    public void Start()
    {
        skipped.Clear();
        retriedSkipped = false;
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

    /// <summary>Called every framework tick. Errors are shown in the status instead of freezing the loop silently.</summary>
    public void Tick()
    {
        if (!Running) return;
        try { TickInner(); }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[HLH] Tick failed");
            DebugLog.Write($"ERROR in {State}: {ex}");
            StatusText = $"Error in {State}: {ex.Message}";
        }
    }

    private void TickInner()
    {
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
        if (queue.Count == 0 && skipped.Count > 0 && !retriedSkipped)
        {
            retriedSkipped = true;
            skipped.Clear();
            queue = BuildQueue();
        }
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
        // Approximate areas: use spawns we've seen ourselves once we have a few.
        if (t.Approximate && learned.AreaFor(t, area) is { } seen) area = seen;
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
        Learn(pos);
        var range = t.Approximate && area!.Observations == 0 ? 90f : ScanRange;
        var found = scanner.FindNearest(t, range);
        if (found == null && combat.InCombat && scanner.FindAttacker(AttackerRange) is { } attacker)
        {
            DebugLog.Write($"ATTACKED by {attacker.Name} while searching, fighting it");
            found = attacker;
        }
        if (found != null && ignored.TryGetValue(found.GameObjectId, out var until) && DateTime.UtcNow < until)
            found = null;
        if (found != null)
        {
            travel.Stop();
            mob = found;
            searchRounds = 0;
            stuckCount = 0;
            approachDest = default;
            lastApproachPath = DateTime.MinValue;
            approachBest = float.MaxValue;
            approachProgressAt = DateTime.UtcNow;
            dismountStarted = DateTime.MinValue;
            Go(HuntState.Approaching, $"Found {t.Name}");
            WaitRandom(0.1, 0.4); // short human-ish reaction time
            return;
        }

        // Chain to the next waypoint just before arriving so movement flows instead of stop-start.
        var nearlyThere = pauseAtDest <= 0 && searchDest is { } d && Flat(pos, d) < 6f;
        if (travel.IsBusy && !nearlyThere)
        {
            CheckStuck(pos);
            return;
        }

        if (pauseAtDest > 0 && !travel.IsBusy)
        {
            waitUntil = DateTime.UtcNow.AddSeconds(pauseAtDest); // brief look-around on arrival
            pauseAtDest = 0;
            return;
        }

        if (waypoints.Count == 0)
        {
            if (++searchRounds > MaxSearchRounds) { Skip("not found"); return; }
            waypoints = new Queue<Waypoint>(search.Waypoints(area!, pos));
            if (waypoints.Count == 0) { Skip("no search points"); return; }
        }

        if (searchDest is { } prev && Flat(pos, prev) < 8f) stuckCount = 0; // arrived: progress

        var next = waypoints.Dequeue();
        // Skip points that aren't on walkable ground; try up to a few before waiting a tick.
        var tries = 0;
        while (!travel.MoveTo(next.Position, allowMount: Flat(pos, next.Position) > 45f))
        {
            DebugLog.Write($"WAYPOINT unwalkable {DebugLog.V(next.Position)}, skipping");
            if (waypoints.Count == 0 || ++tries > 4) { searchDest = null; return; }
            next = waypoints.Dequeue();
        }
        searchDest = next.Position;
        ResetStuck(pos);
        StatusText = $"Searching for {t.Name} (round {searchRounds}/{MaxSearchRounds})";
        pauseAtDest = next.PauseSeconds;
    }

    private const float DismountRange = 20f;
    private const float AttackerRange = 30f;
    private bool pullRetried;
    private DateTime lastPull = DateTime.MinValue;
    private Vector3? searchDest;
    private float pauseAtDest;
    private Vector3 approachDest;
    private float approachBest = float.MaxValue;
    private DateTime approachProgressAt = DateTime.UtcNow;
    private DateTime lastApproachPath = DateTime.MinValue;
    private readonly Dictionary<ulong, DateTime> ignored = new();
    private DateTime dismountStarted = DateTime.MinValue;

    private void Approach(Vector3 pos)
    {
        if (mob == null || mob.IsDead || !mob.IsValid())
        {
            Go(HuntState.Searching, $"Lost {Current!.Target.Name}, searching");
            return;
        }

        var dist = Vector3.Distance(pos, mob.Position) - mob.HitboxRadius;

        // Something else is hitting us on the way: deal with it first (the hunt target is still
        // counted by Verify, which sends us back to searching if the kill wasn't ours).
        if (combat.InCombat && scanner.FindAttacker(AttackerRange) is { } attacker && attacker.GameObjectId != mob.GameObjectId)
        {
            DebugLog.Write($"ATTACKED by {attacker.Name} while approaching {mob.Name} ({dist:F0}y), fighting it first");
            mob = attacker;
            dist = Vector3.Distance(pos, mob.Position) - mob.HitboxRadius;
        }

        // Get off the mount before fighting. Flying needs two presses (land, then dismount),
        // so keep pressing about once a second until the game says we're on foot.
        // In combat with nothing near us (combat flag from a fight we flew away from), keep flying.
        if (travel.IsMounted && (dist <= DismountRange || (combat.InCombat && dist <= AttackerRange)))
        {
            if (dismountStarted == DateTime.MinValue) dismountStarted = DateTime.UtcNow;
            StatusText = $"Dismounting near {mob.Name}";
            if (!travel.TryDismount(mob.Position))
            {
                if (DateTime.UtcNow - dismountStarted > TimeSpan.FromSeconds(20))
                {
                    ignored[mob.GameObjectId] = DateTime.UtcNow.AddSeconds(60);
                    Go(HuntState.Searching, "Could not dismount here, searching");
                }
                return;
            }
        }

        if (combat.InCombat && !travel.IsMounted && dist <= AttackerRange)
        {
            travel.Stop();
            combat.Engage(mob);
            pullRetried = false;
            Go(HuntState.Fighting, $"Fighting {mob.Name}");
            return;
        }

        if (dist > EngageRange)
        {
            StatusText = $"Approaching {mob.Name} ({dist:F0}y)";
            var now = DateTime.UtcNow;
            var mobMoved = Vector3.Distance(mob.Position, approachDest) > 3f;
            if (now - lastApproachPath > TimeSpan.FromSeconds(1.5) && (mobMoved || !travel.IsBusy))
            {
                travel.MoveTo(mob.Position, allowMount: dist > 60f, exactHeight: true);
                approachDest = mob.Position;
                lastApproachPath = now;
                DebugLog.Write($"APPROACH {mob.Name} id {mob.GameObjectId:X} at {DebugLog.V(mob.Position)} dist {dist:F1} best {approachBest:F1} busy={travel.IsBusy} computing={travel.IsComputingPath}");
            }
            // Give up on this mob if we stop getting closer (e.g. pressed against a wall or cliff).
            if (dist < approachBest - 1f) { approachBest = dist; approachProgressAt = now; }
            if (now - approachProgressAt > TimeSpan.FromSeconds(5) || now - stateSince > TimeSpan.FromSeconds(25))
            {
                ignored[mob.GameObjectId] = now.AddSeconds(60);
                travel.Stop();
                combat.StandDown();
                DebugLog.Write($"APPROACH gave up on {mob.Name}: best {approachBest:F1}, now {dist:F1}, {(now - stateSince).TotalSeconds:F0}s");
                Go(HuntState.Searching, $"Could not reach {mob.Name}, looking for another");
                mob = null;
            }
            return;
        }

        travel.Stop();
        combat.Engage(mob);
        pullRetried = false;
        Go(HuntState.Fighting, $"Fighting {mob.Name}");
    }

    private void Fight()
    {
        if (!combat.InCombat && mob != null && !mob.IsDead)
        {
            var waited = DateTime.UtcNow - stateSince;
            if (waited > TimeSpan.FromSeconds(8))
            {
                // This mob won't engage (out of range, line of sight, tagged...). Try another one.
                ignored[mob.GameObjectId] = DateTime.UtcNow.AddSeconds(60);
                combat.StandDown();
                mob = null;
                Go(HuntState.Searching, "Pull failed, looking for another");
                return;
            }
            if (DateTime.UtcNow - lastPull > TimeSpan.FromSeconds(1.5))
            {
                if (Vector3.Distance(Plugin.ObjectTable.LocalPlayer!.Position, mob.Position) - mob.HitboxRadius > EngageRange + 1f)
                    travel.MoveTo(mob.Position, allowMount: false, exactHeight: true);
                combat.Pull(mob);
                lastPull = DateTime.UtcNow;
                DebugLog.Write($"PULL {mob.Name} dist {Vector3.Distance(Plugin.ObjectTable.LocalPlayer!.Position, mob.Position):F1} hitbox {mob.HitboxRadius:F1}");
            }
            return;
        }
        if (combat.InCombat)
        {
            // Keep the hunt target selected; BMR/RSR do the rest.
            if (mob != null && !mob.IsDead && Plugin.TargetManager.Target == null) Plugin.TargetManager.Target = mob;
            combat.KeepAlive(mob);
            return;
        }
        if (mob != null && !mob.IsDead && DateTime.UtcNow - stateSince < TimeSpan.FromSeconds(8)) return; // pull not started yet
        if (DateTime.UtcNow - stateSince < TimeSpan.FromSeconds(1)) return; // combat may not have registered yet
        mob = null;
        combat.StandDown();
        Go(HuntState.Verifying, "Checking hunting log");
    }

    private void Verify()
    {
        var t = Current!.Target;
        var left = progress.KillsRemaining(t);
        if (left < 0) return; // log data not ready, try next tick
        // Move on as soon as the log registers the kill; only wait while the count hasn't changed yet.
        if (left == Current.KillsRemaining && left > 0 && DateTime.UtcNow - stateSince < VerifyTimeout) return;
        if (left == 0)
        {
            Plugin.Chat.Print($"[HLH] Hunting log: {t.Name} complete.");
            Go(HuntState.Choosing, $"{t.Name} complete");
            WaitRandom(0.3, 1.0);
            return;
        }
        Current = Current with { KillsRemaining = left };
        searchRounds = 0;
        waypoints.Clear();
        Go(HuntState.Searching, $"{t.Name}: {left} left");
        WaitRandom(0.1, 0.5);
    }

    /// <summary>Remember where we see any hunting log target, so approximate areas get real spawn points.</summary>
    private void Learn(Vector3 pos)
    {
        if (DateTime.UtcNow - lastLearn < TimeSpan.FromSeconds(1)) return;
        lastLearn = DateTime.UtcNow;
        var terr = (uint)Plugin.ClientState.TerritoryType;
        var wanted = db.All(LogKind.Class).Concat(db.All(LogKind.GrandCompany))
            .Where(t => t.Approximate).Select(t => t.BNpcNameId).ToHashSet();
        foreach (var npc in Plugin.ObjectTable.OfType<IBattleNpc>())
            if (wanted.Contains(npc.NameId) && !npc.IsDead)
                learned.Record(npc.NameId, terr, npc.Position);
        learned.SaveIfDirty();
    }

    private void Skip(string why)
    {
        var t = Current!.Target;
        skipped.Add(t);
        Plugin.Chat.Print($"[HLH] Skipping {t.Name}: {why}.");
        DebugLog.Write($"SKIP {t.Name}: {why}");
        travel.Stop();
        Go(HuntState.Choosing, $"Skipped {t.Name} ({why})");
    }

    private bool CheckStuck(Vector3 pos)
    {
        if (Vector3.Distance(pos, lastPos) > 1.5f) { ResetStuck(pos); return false; }
        if (travel.IsComputingPath) { lastMoved = DateTime.UtcNow; return false; } // standing still while the path is computed
        if (DateTime.UtcNow - lastMoved < StuckWindow) return false;

        stuckCount++;
        ResetStuck(pos);
        DebugLog.Write($"STUCK #{stuckCount} at {DebugLog.V(pos)} heading to {DebugLog.V(searchDest)} mounted={travel.IsMounted}");
        if (stuckCount >= 6) { Stop("Stopped: stuck 6 times in a row"); return true; }
        // First try a hop (looks natural and clears small snags), then give up on this waypoint.
        if (stuckCount % 2 == 1) { travel.Jump(); return false; }
        travel.Stop();
        StatusText = "Snagged, picking another spot";
        return true; // next tick picks the next waypoint
    }

    private void ResetStuck(Vector3 pos) { lastPos = pos; lastMoved = DateTime.UtcNow; }

    private void Go(HuntState s, string status)
    {
        State = s;
        StatusText = status;
        stateSince = DateTime.UtcNow;
        Plugin.Log.Debug($"[HLH] {s}: {status}");
        var me = Plugin.ObjectTable.LocalPlayer;
        DebugLog.Write($"STATE {s} | {status} | terr {Plugin.ClientState.TerritoryType} pos {DebugLog.V(me?.Position)}");
    }

    private void WaitRandom(double minSec, double maxSec) =>
        waitUntil = DateTime.UtcNow.AddSeconds(minSec + rng.NextDouble() * (maxSec - minSec));

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
}
