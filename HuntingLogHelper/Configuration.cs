using Dalamud.Configuration;
using System;

namespace HuntingLogHelper;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Only queue targets within this many levels above the current class level.</summary>
    public int MaxLevelsAbove { get; set; } = 2;

    public bool UseMounts { get; set; } = true;
    public bool UseFlying { get; set; } = true;

    /// <summary>Grand Company log mode. Disabled until the class log is verified in-game.</summary>
    public bool EnableGrandCompanyLog { get; set; } = false;

    /// <summary>Write hlh-debug.log with movement, dismount and combat decisions.</summary>
    public bool DebugLogging { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
