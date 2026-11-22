using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Collections.Generic;
using HuntingLogHelper.Core;
using Lumina.Excel.Sheets;

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

    // Level-1 opener per base class (jobs use their parent class). RSR won't open on passive mobs.
    private static readonly Dictionary<uint, uint> Openers = new()
    {
        [1] = 9,     // Gladiator: Fast Blade
        [2] = 53,    // Pugilist: Bootshine
        [3] = 31,    // Marauder: Heavy Swing
        [4] = 75,    // Lancer: True Thrust
        [5] = 97,    // Archer: Heavy Shot
        [6] = 119,   // Conjurer: Stone
        [7] = 142,   // Thaumaturge: Blizzard
        [26] = 163,  // Arcanist: Ruin
        [29] = 2240, // Rogue: Spinning Edge
    };

    public void Pull(IBattleNpc mob)
    {
        if (InCombat) return;
        Plugin.TargetManager.Target = mob;
        var am = ActionManager.Instance();
        if (am == null) return;

        var opener = OpenerForCurrentClass();
        if (opener != 0 && am->GetActionStatus(ActionType.Action, opener, mob.GameObjectId) == 0)
            am->UseAction(ActionType.Action, opener, mob.GameObjectId);
        else
            am->UseAction(ActionType.GeneralAction, AutoAttack, mob.GameObjectId);
    }

    private static uint OpenerForCurrentClass()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return 0;
        var job = Plugin.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(me.ClassJob.RowId);
        if (job == null) return 0;
        var parent = job.Value.ClassJobParent.RowId;
        return Openers.TryGetValue(parent, out var id) ? id
             : Openers.TryGetValue(me.ClassJob.RowId, out id) ? id : 0;
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
