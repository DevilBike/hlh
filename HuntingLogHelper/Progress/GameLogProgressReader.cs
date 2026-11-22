using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using HuntingLogHelper.Core;
using Lumina.Excel.Sheets;

namespace HuntingLogHelper.Progress;

/// <summary>
/// Reads the hunting log from MonsterNoteManager. The game only keeps kill counts for the
/// rank currently in progress; earlier ranks are complete and later ranks are locked.
/// </summary>
public sealed unsafe class GameLogProgressReader : ILogProgressReader
{
    public int LogIndexForClassJob(uint classJobId)
    {
        var row = Plugin.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(classJobId);
        if (row == null) return -1;
        int idx = row.Value.MonsterNote;
        return idx is >= 0 and < 12 ? idx : -1;
    }

    public int CurrentRank(int logIndex)
    {
        var mgr = MonsterNoteManager.Instance();
        if (mgr == null || logIndex < 0) return -1;
        return mgr->RankData[logIndex].Rank;
    }

    public int KillsRemaining(HuntTarget target)
    {
        var mgr = MonsterNoteManager.Instance();
        if (mgr == null) return -1;

        var info = mgr->RankData[target.LogIndex];
        if (target.Rank < info.Rank) return 0;                   // earlier rank: already done
        if (target.Rank > info.Rank) return target.RequiredKills; // later rank: locked, nothing counted

        int kills = info.RankData[target.Entry][target.Slot];
        return Math.Max(0, target.RequiredKills - kills);
    }
}
