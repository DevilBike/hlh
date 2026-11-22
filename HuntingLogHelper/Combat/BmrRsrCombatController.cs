using Dalamud.Game.ClientState.Objects.Types;
using HuntingLogHelper.Core;

namespace HuntingLogHelper.Combat;

/// <summary>Drives BossMod Reborn AI and Rotation Solver Reborn through their commands. Implemented in step 3.</summary>
public sealed class BmrRsrCombatController : ICombatController
{
    public bool InCombat => Plugin.ClientState.LocalPlayer?.StatusFlags.HasFlag(
        Dalamud.Game.ClientState.Objects.Enums.StatusFlags.InCombat) ?? false;

    public void Engage(IBattleNpc mob) { /* TODO(step 3): target mob, /rotation auto, /bmrai on */ }
    public void Disengage() { /* TODO(step 3): /rotation off, /bmrai off */ }
}
