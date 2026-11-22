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

    public bool IsInZone(uint territoryId) => Plugin.ClientState.TerritoryType == territoryId;

    public void TravelToZone(HuntArea area)
    {
        Stop();
        if (Try(() => lifestreamTeleport.InvokeFunc(area.AetheryteId, 0), false)) return;
        var telepo = Telepo.Instance();
        if (telepo != null) telepo->Teleport(area.AetheryteId, 0);
    }

    public void MoveTo(Vector3 destination, bool allowMount, bool exactHeight = false)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return;

        // Map waypoints have no height: drop them onto the floor. Real positions (mobs) only need
        // the nearest walkable point, so we don't snap onto a rock or ledge above them.
        var snapped = exactHeight
            ? Try(() => nearestPoint.InvokeFunc(destination, 3f, 5f), null)
            : Try(() => pointOnFloor.InvokeFunc(destination with { Y = me.Position.Y + 50f }, false, 8f), null);
        var target = snapped ?? destination;

        var far = Vector3.Distance(me.Position, target) > MountDistance;
        if (allowMount && far && config.UseMounts && !Mounted && !Plugin.Condition[ConditionFlag.InCombat])
        {
            pendingDestination = target;
            UseGeneralAction(MountRouletteAction);
            mountRequestedAt = DateTime.UtcNow;
            return; // Update() starts the path once mounted
        }

        StartPath(target);
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

    public void Stop()
    {
        pendingDestination = null;
        Try(() => { pathStop.InvokeAction(); return true; }, false);
    }

    private void StartPath(Vector3 target)
    {
        var fly = config.UseFlying && Mounted && CanFlyHere();
        Try(() => pathfindAndMoveTo.InvokeFunc(target, fly), false);
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
