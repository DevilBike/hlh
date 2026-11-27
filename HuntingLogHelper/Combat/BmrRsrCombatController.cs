using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
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
        TurnOn();
        DebugLog.Write($"ENGAGE {mob.Name} hp {mob.CurrentHp}/{mob.MaxHp}");
        Pull(mob);
        ResetWatch(mob);
    }

    private static void TurnOn()
    {
        Plugin.CommandManager.ProcessCommand("/rotation auto");
        Plugin.CommandManager.ProcessCommand("/bmrai on");
    }

    // Rotation watchdog: RSR sometimes switches itself off mid-run (its own auto-off rules, a
    // cutscene/loading flag, another plugin). If the fight makes no progress for a while,
    // send the commands again. Both commands set a state (not toggle), so re-sending is safe.
    private static readonly TimeSpan StallWindow = TimeSpan.FromSeconds(5);
    private ulong watchedId;
    private uint watchedHp;
    private DateTime lastProgress = DateTime.UtcNow;
    private DateTime lastRevive = DateTime.MinValue;

    private void ResetWatch(IBattleNpc? mob)
    {
        watchedId = mob?.GameObjectId ?? 0;
        watchedHp = mob?.CurrentHp ?? 0;
        lastProgress = DateTime.UtcNow;
    }

    public void KeepAlive(IBattleNpc? mob)
    {
        var now = DateTime.UtcNow;
        var me = Plugin.ObjectTable.LocalPlayer;
        // Fight whatever is actually hitting us if our hunt target isn't selectable any more.
        var target = Plugin.TargetManager.Target as IBattleNpc ?? mob;
        if (target == null || target.IsDead) { ResetWatch(null); return; }
        if (target.GameObjectId != watchedId) { ResetWatch(target); return; }
        if (target.CurrentHp < watchedHp || Plugin.Condition[ConditionFlag.Casting])
        {
            watchedHp = Math.Min(watchedHp, target.CurrentHp);
            lastProgress = now;
            return;
        }
        if (now - lastProgress < StallWindow || now - lastRevive < StallWindow) return;

        lastRevive = now;
        lastProgress = now;
        var dist = me == null ? -1f : System.Numerics.Vector3.Distance(me.Position, target.Position);
        DebugLog.Write($"ROTATION stalled on {target.Name} (hp {target.CurrentHp}/{target.MaxHp}, dist {dist:F1}, target={(Plugin.TargetManager.Target?.Name.TextValue ?? "none")}), re-enabling");
        Plugin.TargetManager.Target = target;
        TurnOn();
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
