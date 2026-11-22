using System.Numerics;
using HuntingLogHelper.Core;

namespace HuntingLogHelper.Travel;

/// <summary>Uses Lifestream for zone travel and vnavmesh for movement, mounting and flying per config. Implemented in step 3.</summary>
public sealed class IpcTravelService : ITravelService
{
    private readonly Configuration config;
    public IpcTravelService(Configuration config) => this.config = config;

    public bool IsBusy => false; // TODO(step 3): Lifestream.IsBusy || vnavmesh.Path.IsRunning
    public bool IsInZone(uint territoryId) => Plugin.ClientState.TerritoryType == territoryId;
    public void TravelToZone(uint territoryId) { /* TODO(step 3) */ }
    public void MoveTo(Vector3 destination) { /* TODO(step 3): mount if config.UseMounts, fly if config.UseFlying */ }
    public void Stop() { /* TODO(step 3): vnavmesh.Path.Stop */ }
}
