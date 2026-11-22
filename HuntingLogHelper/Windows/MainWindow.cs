using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using HuntingLogHelper.Core;
using System;
using System.Numerics;

namespace HuntingLogHelper.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin) : base("Hunting Log Helper##main")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        var engine = plugin.Engine;

        if (engine.Running)
        {
            if (ImGui.Button("Stop")) engine.Stop();
        }
        else if (ImGui.Button("Start class hunting log"))
        {
            engine.Start();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(engine.StatusText);

        if (engine.Current is { } cur)
            ImGui.TextUnformatted($"Target: {cur.Target} - {cur.KillsRemaining}/{cur.Target.RequiredKills} left"
                + (cur.Target.Approximate ? " (approximate area)" : ""));

        ImGui.Separator();
        if (ImGui.CollapsingHeader("Settings"))
        {
            var levels = cfg.MaxLevelsAbove;
            if (ImGui.SliderInt("Max levels above you", ref levels, 0, 10)) { cfg.MaxLevelsAbove = levels; cfg.Save(); }

            var mounts = cfg.UseMounts;
            if (ImGui.Checkbox("Use mounts", ref mounts)) { cfg.UseMounts = mounts; cfg.Save(); }

            var flying = cfg.UseFlying;
            if (ImGui.Checkbox("Fly where unlocked", ref flying)) { cfg.UseFlying = flying; cfg.Save(); }

            ImGui.BeginDisabled();
            var gc = cfg.EnableGrandCompanyLog;
            ImGui.Checkbox("Grand Company log (after class log is verified)", ref gc);
            ImGui.EndDisabled();
        }

        if (ImGui.CollapsingHeader("Queue for current class"))
        {
            var queue = engine.BuildQueue();
            if (queue.Count == 0) ImGui.TextUnformatted("Nothing queued (no hunting log for this class, or all done at your level).");
            foreach (var q in queue)
                ImGui.TextUnformatted($"Lv{q.Target.Level,-3} {q.Target.Name} - {q.KillsRemaining} left ({string.Join(", ", q.Target.ZoneNames)})");
        }

        if (ImGui.CollapsingHeader("Debug: raw hunting log"))
            DrawDebug();
    }

    private void DrawDebug()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return;
        var reader = plugin.Progress;
        var idx = reader.LogIndexForClassJob(me.ClassJob.RowId);
        ImGui.TextUnformatted($"ClassJob {me.ClassJob.RowId}, log index {idx}, current rank (0-based) {reader.CurrentRank(idx)}");
        ImGui.TextUnformatted($"Territory {Plugin.ClientState.TerritoryType}, position {me.Position:F1}");
        foreach (var t in plugin.Targets.All(LogKind.Class))
        {
            if (t.LogIndex != idx || t.Rank != reader.CurrentRank(idx)) continue;
            ImGui.TextUnformatted($"Entry {t.Entry + 1} slot {t.Slot}: {t.Name} needs {t.RequiredKills}, remaining {reader.KillsRemaining(t)}");
        }
    }
}
