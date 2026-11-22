using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using HuntingLogHelper.Core;

namespace HuntingLogHelper.Combat;

/// <summary>
/// Targets the mob, starts the pull with auto-attack (RSR won't open on passive mobs),
/// and hands the fight to Rotation Solver Reborn and BossMod Reborn's AI.
/// </summary>
public sealed unsafe class BmrRsrCombatController : ICombatController
{
    private const uint AutoAttack = 1; // GeneralAction: Auto-attack

    public bool InCombat => Plugin.Condition[ConditionFlag.InCombat];

    public void Engage(IBattleNpc mob)
    {
        Plugin.TargetManager.Target = mob;
        // RSR switches itself off on zone change, so (re)send once per engage, never per frame.
        Plugin.CommandManager.ProcessCommand("/rotation auto");
        Plugin.CommandManager.ProcessCommand("/bmrai on");
        Pull(mob);
    }

    public void Pull(IBattleNpc mob)
    {
        if (InCombat) return;
        var am = ActionManager.Instance();
        if (am != null) am->UseAction(ActionType.GeneralAction, AutoAttack, mob.GameObjectId);
    }

    public void StandDown()
    {
        // BMR AI walks straight toward targets/leader outside combat (no navmesh), which runs into walls.
        Plugin.CommandManager.ProcessCommand("/bmrai off");
        Plugin.TargetManager.Target = null;
    }

    public void Disengage()
    {
        Plugin.CommandManager.ProcessCommand("/rotation off");
        Plugin.CommandManager.ProcessCommand("/bmrai off");
    }
}
