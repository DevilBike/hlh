using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using HuntingLogHelper.Core;

namespace HuntingLogHelper.Combat;

/// <summary>
/// Targets the mob and hands the fight to Rotation Solver Reborn (rotation) and
/// BossMod Reborn's AI (positioning, dodging), using their chat commands.
/// </summary>
public sealed class BmrRsrCombatController : ICombatController
{
    public bool InCombat => Plugin.Condition[ConditionFlag.InCombat];

    public void Engage(IBattleNpc mob)
    {
        Plugin.TargetManager.Target = mob;
        // Always (re)send: RSR switches itself off on zone change and other events,
        // so we can't trust that an earlier "on" is still in effect.
        Plugin.CommandManager.ProcessCommand("/rotation auto");
        Plugin.CommandManager.ProcessCommand("/bmrai on");
    }

    public void Disengage()
    {
        Plugin.CommandManager.ProcessCommand("/rotation off");
        Plugin.CommandManager.ProcessCommand("/bmrai off");
    }
}
