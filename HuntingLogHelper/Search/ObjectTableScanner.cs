using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using HuntingLogHelper.Core;

namespace HuntingLogHelper.Search;

/// <summary>Finds the closest living, untagged mob whose name ID matches the target.</summary>
public sealed class ObjectTableScanner : ITargetScanner
{
    public IBattleNpc? FindNearest(HuntTarget target)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return null;

        return Plugin.ObjectTable
            .OfType<IBattleNpc>()
            .Where(n => n.NameId == target.BNpcNameId && !n.IsDead && n.IsTargetable)
            .OrderBy(n => System.Numerics.Vector3.Distance(n.Position, me.Position))
            .FirstOrDefault();
    }
}
