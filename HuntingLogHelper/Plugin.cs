using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.IO;
using HuntingLogHelper.Combat;
using HuntingLogHelper.Core;
using HuntingLogHelper.Data;
using HuntingLogHelper.Engine;
using HuntingLogHelper.Progress;
using HuntingLogHelper.Search;
using HuntingLogHelper.Travel;
using HuntingLogHelper.Windows;

namespace HuntingLogHelper;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;

    private const string CommandName = "/huntlog";

    public Configuration Configuration { get; }
    public HuntEngine Engine { get; }
    public ITargetDatabase Targets { get; }
    public ILogProgressReader Progress { get; }
    public readonly WindowSystem WindowSystem = new("HuntingLogHelper");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Composition root: the only place that knows which implementation backs each module.
        var dataDir = Path.Combine(PluginInterface.AssemblyLocation.DirectoryName!, "Data");
        Targets = new JsonTargetDatabase(dataDir);
        Progress = new GameLogProgressReader();
        Engine = new HuntEngine(
            Targets,
            Progress,
            new IpcTravelService(Configuration),
            new WanderSearchPattern(),
            new ObjectTableScanner(),
            new BmrRsrCombatController(),
            Configuration);
        Framework.Update += OnFrameworkUpdate;

        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the window. /huntlog start | stop",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

        Log.Information("Hunting Log Helper loaded.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        Engine.Stop();
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        WindowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    private void OnFrameworkUpdate(IFramework _) => Engine.Tick();

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "start": Engine.Start(); break;
            case "stop": Engine.Stop(); break;
            default: ToggleMainUi(); break;
        }
    }

    public void ToggleMainUi() => mainWindow.Toggle();
}
