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
    private bool engaged;

    public bool InCombat => Plugin.Condition[ConditionFlag.InCombat];

    public void Engage(IBattleNpc mob)
    {
        Plugin.TargetManager.Target = mob;
        if (engaged) return;
        Plugin.CommandManager.ProcessCommand("/rotation auto");
        Plugin.CommandManager.ProcessCommand("/bmrai on");
        engaged = true;
    }

    public void Disengage()
    {
        if (!engaged) return;
        Plugin.CommandManager.ProcessCommand("/rotation off");
        Plugin.CommandManager.ProcessCommand("/bmrai off");
        engaged = false;
    }
}
