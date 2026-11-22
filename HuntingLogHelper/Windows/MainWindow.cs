using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
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
            MinimumSize = new Vector2(360, 260),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        ImGui.TextUnformatted($"Status: {plugin.Engine.StatusText}");
        ImGui.Separator();

        var levels = cfg.MaxLevelsAbove;
        if (ImGui.SliderInt("Max levels above you", ref levels, 0, 10))
        {
            cfg.MaxLevelsAbove = levels;
            cfg.Save();
        }

        var mounts = cfg.UseMounts;
        if (ImGui.Checkbox("Use mounts", ref mounts)) { cfg.UseMounts = mounts; cfg.Save(); }

        var flying = cfg.UseFlying;
        if (ImGui.Checkbox("Fly where unlocked", ref flying)) { cfg.UseFlying = flying; cfg.Save(); }

        ImGui.BeginDisabled();
        var gc = cfg.EnableGrandCompanyLog;
        ImGui.Checkbox("Grand Company log (coming after class log is verified)", ref gc);
        ImGui.EndDisabled();
    }
}
