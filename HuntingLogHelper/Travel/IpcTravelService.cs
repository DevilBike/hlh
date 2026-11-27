using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using HuntingLogHelper.Core;
using Lumina.Excel.Sheets;

namespace HuntingLogHelper.Travel;

/// <summary>
/// Zone travel through Lifestream (falls back to the game's own teleport), and movement
/// through vnavmesh. Mounts for long trips and flies where the zone's aether currents are done.
/// </summary>
public sealed unsafe class IpcTravelService : ITravelService
{
    private const uint MountRouletteAction = 9;   // GeneralAction: Mount Roulette
    private const uint DismountAction = 23;       // GeneralAction: Dismount
    private const float MountDistance = 45f;

    private readonly Configuration config;

    private readonly ICallGateSubscriber<bool> navIsReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> pathfindAndMoveTo;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPoint;
    private readonly ICallGateSubscriber<uint, byte, bool> lifestreamTeleport;
    private readonly ICallGateSubscriber<bool> lifestreamIsBusy;

    private Vector3? pendingDestination;
    private DateTime mountRequestedAt = DateTime.MinValue;
    private DateTime lastMountPress = DateTime.MinValue;
    private int mountPresses;

    // The current long trip that is allowed to use a mount, so we can mount mid-route if the
    // first attempt failed (e.g. right after a teleport the game rejects the action for a moment).
    private Vector3? mountTrip;
    private DateTime lastMidRouteMount = DateTime.MinValue;

    public IpcTravelService(Configuration config)
    {
        this.config = config;
        var pi = Plugin.PluginInterface;
        navIsReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        pathfindAndMoveTo = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        pathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        pathStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        pointOnFloor = pi.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        nearestPoint = pi.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
        lifestreamTeleport = pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        lifestreamIsBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
    }

    public bool IsReady => Try(() => navIsReady.InvokeFunc(), false);

    public bool IsBusy =>
        pendingDestination != null
        || Plugin.Condition[ConditionFlag.BetweenAreas]
        || Plugin.Condition[ConditionFlag.BetweenAreas51]
        || Plugin.Condition[ConditionFlag.Casting]
        || Try(() => lifestreamIsBusy.InvokeFunc(), false)
        || Try(() => pathfindInProgress.InvokeFunc(), false)
        || Try(() => pathIsRunning.InvokeFunc(), false);

    public bool IsMounted => Mounted;

    // Mounting up counts as "getting ready to move", so the engine doesn't call it stuck.
    public bool IsComputingPath => pendingDestination != null || Try(() => pathfindInProgress.InvokeFunc(), false);

    public void Jump() => UseGeneralAction(2); // GeneralAction: Jump

    public bool IsInZone(uint territoryId) => Plugin.ClientState.TerritoryType == territoryId;

    public void TravelToZone(HuntArea area)
    {
        Stop();
        if (Try(() => lifestreamTeleport.InvokeFunc(area.AetheryteId, 0), false)) return;
        var telepo = Telepo.Instance();
        if (telepo != null) telepo->Teleport(area.AetheryteId, 0);
    }

    public bool MoveTo(Vector3 destination, bool allowMount, bool exactHeight = false)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return false;

        // Map waypoints have no height: drop them onto the floor. Real positions (mobs) only need
        // the nearest walkable point, so we don't snap onto a rock or ledge above them.
        var snapped = exactHeight
            ? Try(() => nearestPoint.InvokeFunc(destination, 3f, 5f), null)
            : Try(() => pointOnFloor.InvokeFunc(destination with { Y = me.Position.Y + 50f }, false, 8f), null)
              ?? Try(() => nearestPoint.InvokeFunc(destination with { Y = me.Position.Y }, 12f, 60f), null);
        DebugLog.Write($"MOVETO want {DebugLog.V(destination)} snapped {DebugLog.V(snapped)} exactHeight={exactHeight} from {DebugLog.V(me.Position)} mounted={Mounted}");
        if (snapped == null && !exactHeight) return false; // not walkable (rock, water, cliff): skip it
        var target = snapped ?? destination;

        var far = Vector3.Distance(me.Position, target) > MountDistance;
        mountTrip = allowMount && far ? target : null;
        if (allowMount && far && CanMountNow())
        {
            pendingDestination = target;
            mountRequestedAt = DateTime.UtcNow;
            mountPresses = 0;
            lastMountPress = DateTime.MinValue;
            PressMount();
            return true; // Update() starts the path once mounted
        }
        if (allowMount && far)
            DebugLog.Write($"MOUNT skipped: useMounts={config.UseMounts} mounted={Mounted} inCombat={Plugin.Condition[ConditionFlag.InCombat]} status={MountStatus()}");

        StartPath(target);
        return true;
    }

    private bool CanMountNow() => config.UseMounts && !Mounted && !Plugin.Condition[ConditionFlag.InCombat];

    private static uint MountStatus()
    {
        var am = ActionManager.Instance();
        return am == null ? uint.MaxValue : am->GetActionStatus(ActionType.GeneralAction, MountRouletteAction);
    }

    private void PressMount()
    {
        var status = MountStatus();
        lastMountPress = DateTime.UtcNow;
        if (status != 0)
        {
            // Not usable yet (still landing from a teleport, animation lock...). Update() retries.
            DebugLog.Write($"MOUNT not ready (status {status}), will retry");
            return;
        }
        mountPresses++;
        UseGeneralAction(MountRouletteAction);
        DebugLog.Write($"MOUNT pressed (try {mountPresses})");
    }

    /// <summary>Called every frame by the engine so a pending mount-then-move can finish.</summary>
    public void Update()
    {
        var now = DateTime.UtcNow;
        if (pendingDestination is { } dest)
        {
            var waited = now - mountRequestedAt;
            var casting = Plugin.Condition[ConditionFlag.Casting];
            if (Mounted || waited > TimeSpan.FromSeconds(8) || Plugin.Condition[ConditionFlag.InCombat])
            {
                if (!Mounted) DebugLog.Write($"MOUNT gave up after {waited.TotalSeconds:F1}s ({mountPresses} presses), walking");
                pendingDestination = null;
                StartPath(dest);
            }
            else if (!casting && now - lastMountPress > TimeSpan.FromSeconds(1.5))
            {
                PressMount(); // the press didn't take: try again
            }
            return;
        }

        // Walking a long trip without a mount (first attempt failed, or we were in combat then):
        // stop briefly, mount, and carry on.
        if (mountTrip is { } trip && CanMountNow() && now - lastMidRouteMount > TimeSpan.FromSeconds(6)
            && !Plugin.Condition[ConditionFlag.Casting]
            && Plugin.ObjectTable.LocalPlayer is { } me
            && Vector3.Distance(me.Position, trip) > MountDistance + 15f
            && MountStatus() == 0)
        {
            lastMidRouteMount = now;
            DebugLog.Write($"MOUNT mid-route, {Vector3.Distance(me.Position, trip):F0}y left");
            Try(() => { pathStop.InvokeAction(); return true; }, false);
            pendingDestination = trip;
            mountRequestedAt = now;
            mountPresses = 0;
            PressMount();
        }
    }

    public void Dismount()
    {
        if (Mounted) UseGeneralAction(DismountAction);
    }

    private readonly Random rng = new();
    private DateTime lastDismountPress = DateTime.MinValue;
    private int failedPresses;
    private Vector3? landingSpot;
    private float lastAltitude = float.MaxValue;
    private DateTime lastDescent = DateTime.UtcNow;

    public bool TryDismount(Vector3 toward)
    {
        if (!Mounted)
        {
            if (failedPresses > 0 || landingSpot != null) DebugLog.Write("DISMOUNT done");
            failedPresses = 0;
            landingSpot = null;
            lastAltitude = float.MaxValue;
            return true;
        }
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return false;
        var airborne = Plugin.Condition[ConditionFlag.InFlight] || Plugin.Condition[ConditionFlag.Diving];

        // Flying to a landing spot we picked.
        if (landingSpot is { } spot)
        {
            if (Vector2.Distance(new(me.Position.X, me.Position.Z), new(spot.X, spot.Z)) > 2f)
            {
                if (!IsBusy) StartPath(spot with { Y = spot.Y + 2f }, fly: airborne);
                return false;
            }
            Stop();
            landingSpot = null;
            lastAltitude = float.MaxValue;
            lastDescent = DateTime.UtcNow;
        }

        if (airborne)
        {
            // Pressing Dismount in the air makes the mount descend and land. The descent takes a few
            // seconds and is NOT a failure; only count a failure if we stop getting lower.
            if (me.Position.Y < lastAltitude - 0.3f) { lastAltitude = me.Position.Y; lastDescent = DateTime.UtcNow; }
            if (DateTime.UtcNow - lastDismountPress > TimeSpan.FromSeconds(2))
            {
                var floor = Try(() => pointOnFloor.InvokeFunc(me.Position, false, 5f), null);
                DebugLog.Write($"DISMOUNT airborne at {DebugLog.V(me.Position)} floorBelow {DebugLog.V(floor)} failed={failedPresses}");
                UseGeneralAction(DismountAction);
                lastDismountPress = DateTime.UtcNow;
            }
            if (DateTime.UtcNow - lastDescent > TimeSpan.FromSeconds(4))
            {
                failedPresses++;
                lastDescent = DateTime.UtcNow;
                DebugLog.Write($"DISMOUNT not descending (alt {me.Position.Y:F1}), failure {failedPresses}");
            }
        }
        else if (DateTime.UtcNow - lastDismountPress > TimeSpan.FromSeconds(1.2))
        {
            // On the ground and still mounted: each press that doesn't work is a real failure.
            if (lastDismountPress != DateTime.MinValue && DateTime.UtcNow - lastDismountPress < TimeSpan.FromSeconds(3)) failedPresses++;
            DebugLog.Write($"DISMOUNT grounded at {DebugLog.V(me.Position)} failed={failedPresses}");
            UseGeneralAction(DismountAction);
            lastDismountPress = DateTime.UtcNow;
        }

        if (failedPresses >= 3)
        {
            // Can't land/dismount here: move to checked, landable ground a few yalms toward the mob.
            var dir = new Vector3(toward.X - me.Position.X, 0, toward.Z - me.Position.Z);
            dir = dir.LengthSquared() < 0.01f ? new Vector3(1, 0, 0) : Vector3.Normalize(dir);
            var angle = (float)((rng.NextDouble() - 0.5) * Math.PI / 2);
            var rot = new Vector3(dir.X * MathF.Cos(angle) - dir.Z * MathF.Sin(angle), 0, dir.X * MathF.Sin(angle) + dir.Z * MathF.Cos(angle));
            var candidate = me.Position + rot * (6f + (float)rng.NextDouble() * 6f);
            var ground = Try(() => pointOnFloor.InvokeFunc(candidate with { Y = me.Position.Y + 5f }, false, 6f), null);
            DebugLog.Write($"DISMOUNT relocating: candidate {DebugLog.V(candidate)} ground {DebugLog.V(ground)}");
            if (ground != null)
            {
                landingSpot = ground;
                StartPath(ground.Value with { Y = ground.Value.Y + 2f }, fly: airborne);
            }
            failedPresses = 0;
        }
        return false;
    }

    public void Stop()
    {
        pendingDestination = null;
        mountTrip = null;
        Try(() => { pathStop.InvokeAction(); return true; }, false);
    }

    private void StartPath(Vector3 target, bool? fly = null)
    {
        var useFly = fly ?? (config.UseFlying && Mounted && CanFlyHere());
        var ok = Try(() => pathfindAndMoveTo.InvokeFunc(target, useFly), false);
        DebugLog.Write($"PATH to {DebugLog.V(target)} fly={useFly} accepted={ok}");
    }

    private static bool Mounted => Plugin.Condition[ConditionFlag.Mounted];

    private static bool CanFlyHere()
    {
        var terr = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(Plugin.ClientState.TerritoryType);
        if (terr == null) return false;
        var set = terr.Value.AetherCurrentCompFlgSet.RowId;
        if (set == 0) return false;
        var ps = PlayerState.Instance();
        return ps != null && ps->IsAetherCurrentZoneComplete(set);
    }

    private static void UseGeneralAction(uint id)
    {
        var am = ActionManager.Instance();
        if (am != null) am->UseAction(ActionType.GeneralAction, id);
    }

    private static T Try<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch (Exception) { return fallback; } // dependency plugin missing or not loaded yet
    }
}
