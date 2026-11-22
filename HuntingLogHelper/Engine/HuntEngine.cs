using System.Collections.Generic;
using System.Linq;
using HuntingLogHelper.Core;

namespace HuntingLogHelper.Engine;

public enum HuntState { Idle, Choosing, Traveling, Searching, Fighting, Verifying, Done, Stopped }

/// <summary>
/// The hunt loop. Only knows the Core interfaces, so each module can change independently.
/// Choose next target -> travel -> search -> fight -> verify log entry complete -> repeat.
/// </summary>
public sealed class HuntEngine
{
    private readonly ITargetDatabase db;
    private readonly ILogProgressReader progress;
    private readonly ITravelService travel;
    private readonly ISearchPattern search;
    private readonly ITargetScanner scanner;
    private readonly ICombatController combat;
    private readonly Configuration config;

    public HuntState State { get; private set; } = HuntState.Idle;
    public QueuedTarget? Current { get; private set; }
    public string StatusText { get; private set; } = "Idle";

    public HuntEngine(ITargetDatabase db, ILogProgressReader progress, ITravelService travel,
        ISearchPattern search, ITargetScanner scanner, ICombatController combat, Configuration config)
    {
        this.db = db; this.progress = progress; this.travel = travel;
        this.search = search; this.scanner = scanner; this.combat = combat; this.config = config;
    }

    /// <summary>Incomplete class-log targets the player can reach, closest to their level first.</summary>
    public IEnumerable<QueuedTarget> BuildQueue(int playerLevel) =>
        db.All(LogKind.Class)
          .Where(t => !t.InDuty && t.Level <= playerLevel + config.MaxLevelsAbove)
          .Select(t => new QueuedTarget(t, progress.KillsRemaining(t)))
          .Where(q => q.KillsRemaining > 0)
          .OrderBy(q => System.Math.Abs(q.Target.Level - playerLevel));

    public void Start() { State = HuntState.Choosing; StatusText = "Starting"; }

    public void Stop()
    {
        travel.Stop();
        combat.Disengage();
        State = HuntState.Stopped;
        StatusText = "Stopped";
    }

    /// <summary>Called every framework tick. State transitions are implemented in step 3.</summary>
    public void Tick() { }
}
