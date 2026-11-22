using HuntingLogHelper.Core;

namespace HuntingLogHelper.Progress;

/// <summary>Reads kill counts from the game's MonsterNote data. Implemented in step 2.</summary>
public sealed class GameLogProgressReader : ILogProgressReader
{
    public int KillsRemaining(HuntTarget target)
    {
        // TODO(step 2): read FFXIVClientStructs MonsterNoteManager for this class/rank.
        return target.RequiredKills;
    }
}
