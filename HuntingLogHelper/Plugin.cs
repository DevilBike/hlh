using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.IO;
using HuntingLogHelper.Combat;
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

    private const string CommandName = "/huntlog";

    public Configuration Configuration { get; }
    public HuntEngine Engine { get; }
    public readonly WindowSystem WindowSystem = new("HuntingLogHelper");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Composition root: the only place that knows which implementation backs each module.
        var dataDir = Path.Combine(PluginInterface.AssemblyLocation.DirectoryName!, "Data");
        Engine = new HuntEngine(
            new JsonTargetDatabase(dataDir),
            new GameLogProgressReader(),
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
            HelpMessage = "Open the Hunting Log Helper window.",
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

    private void OnCommand(string command, string args) => ToggleMainUi();

    public void ToggleMainUi() => mainWindow.Toggle();
}
