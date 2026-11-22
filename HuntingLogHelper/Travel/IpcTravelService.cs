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

    public bool IsComputingPath => Try(() => pathfindInProgress.InvokeFunc(), false);

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
        if (snapped == null && !exactHeight) return false; // not walkable (rock, water, cliff): skip it
        var target = snapped ?? destination;

        var far = Vector3.Distance(me.Position, target) > MountDistance;
        if (allowMount && far && config.UseMounts && !Mounted && !Plugin.Condition[ConditionFlag.InCombat])
        {
            pendingDestination = target;
            UseGeneralAction(MountRouletteAction);
            mountRequestedAt = DateTime.UtcNow;
            return true; // Update() starts the path once mounted
        }

        StartPath(target);
        return true;
    }

    /// <summary>Called every frame by the engine so a pending mount-then-move can finish.</summary>
    public void Update()
    {
        if (pendingDestination is not { } dest) return;
        var waited = DateTime.UtcNow - mountRequestedAt;
        if (Mounted || waited > TimeSpan.FromSeconds(4))
        {
            if (!Plugin.Condition[ConditionFlag.Casting] || waited > TimeSpan.FromSeconds(4))
            {
                pendingDestination = null;
                StartPath(dest);
            }
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

    public bool TryDismount(Vector3 toward)
    {
        if (!Mounted)
        {
            failedPresses = 0;
            landingSpot = null;
            return true;
        }
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return false;

        // Still flying to the landing spot we picked.
        if (landingSpot is { } spot)
        {
            if (Vector2.Distance(new(me.Position.X, me.Position.Z), new(spot.X, spot.Z)) > 1.5f)
            {
                if (!IsBusy) StartPath(spot, fly: true);
                return false;
            }
            Stop();
            landingSpot = null;
        }

        if (DateTime.UtcNow - lastDismountPress < TimeSpan.FromSeconds(1)) return false;

        // Check the ground below before pressing: find landable floor near us.
        var floor = Try(() => pointOnFloor.InvokeFunc(me.Position, false, 4f), null);
        var airborne = Plugin.Condition[ConditionFlag.InFlight] || Plugin.Condition[ConditionFlag.Diving];
        if (airborne && floor is { } f && me.Position.Y - f.Y > 1.5f && failedPresses == 0)
        {
            landingSpot = f;            // descend onto checked ground, then dismount
            StartPath(f, fly: true);
            return false;
        }

        if (failedPresses >= 3)
        {
            // Dismount keeps failing (bad ground, slope, water): move a few yalms toward the mob and retry.
            var dir = Vector3.Normalize(new Vector3(toward.X - me.Position.X, 0, toward.Z - me.Position.Z));
            if (float.IsNaN(dir.X)) dir = new Vector3(1, 0, 0);
            var angle = (float)((rng.NextDouble() - 0.5) * Math.PI / 2);
            var rot = new Vector3(dir.X * MathF.Cos(angle) - dir.Z * MathF.Sin(angle), 0, dir.X * MathF.Sin(angle) + dir.Z * MathF.Cos(angle));
            var candidate = me.Position + rot * (5f + (float)rng.NextDouble() * 5f);
            var ground = Try(() => pointOnFloor.InvokeFunc(candidate with { Y = me.Position.Y + 20f }, false, 5f), null);
            landingSpot = ground ?? candidate;
            failedPresses = 0;
            StartPath(landingSpot.Value, fly: airborne);
            return false;
        }

        UseGeneralAction(DismountAction);
        lastDismountPress = DateTime.UtcNow;
        failedPresses++;
        return false;
    }

    public void Stop()
    {
        pendingDestination = null;
        Try(() => { pathStop.InvokeAction(); return true; }, false);
    }

    private void StartPath(Vector3 target, bool? fly = null)
    {
        var useFly = fly ?? (config.UseFlying && Mounted && CanFlyHere());
        Try(() => pathfindAndMoveTo.InvokeFunc(target, useFly), false);
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
