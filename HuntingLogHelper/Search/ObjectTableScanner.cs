using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using HuntingLogHelper.Core;

namespace HuntingLogHelper.Search;

/// <summary>
/// Finds the closest living mob whose name ID matches the target, skipping ones already
/// fighting another player.
/// </summary>
public sealed class ObjectTableScanner : ITargetScanner
{
    public IBattleNpc? FindNearest(HuntTarget target, float maxDistance)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return null;

        return Plugin.ObjectTable
            .OfType<IBattleNpc>()
            .Where(n => n.NameId == target.BNpcNameId && !n.IsDead && n.IsTargetable && n.CurrentHp > 0)
            .Where(n => n.TargetObjectId == 0 || n.TargetObjectId == me.GameObjectId || n.TargetObjectId == 0xE0000000)
            .Select(n => (mob: n, dist: Vector3.Distance(n.Position, me.Position)))
            .Where(x => x.dist <= maxDistance)
            .OrderBy(x => x.dist)
            .Select(x => x.mob)
            .FirstOrDefault();
    }

    public IBattleNpc? FindAttacker(float maxDistance)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return null;
        return Plugin.ObjectTable
            .OfType<IBattleNpc>()
            .Where(n => n.TargetObjectId == me.GameObjectId && !n.IsDead && n.IsTargetable && n.CurrentHp > 0
                        && (byte)n.BattleNpcKind == 5) // 5 = Combatant (enemy)
            .Select(n => (mob: n, dist: Vector3.Distance(n.Position, me.Position)))
            .Where(x => x.dist <= maxDistance)
            .OrderBy(x => x.dist)
            .Select(x => x.mob)
            .FirstOrDefault();
    }
}
