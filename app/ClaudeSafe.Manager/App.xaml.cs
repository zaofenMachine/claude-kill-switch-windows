using Microsoft.UI.Xaml;

namespace ClaudeSafe.Manager;

public partial class App : Application
{
    private readonly ManagerService manager;
    private readonly LaunchOptions options;
    private MainWindow? window;
    internal App(ManagerService manager, LaunchOptions options)
    {
        this.manager = manager;
        this.options = options;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            if (options.DiagnosticDirectory is { } directory)
                try { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "ui-error.txt"), e.Message + Environment.NewLine + e.Exception); } catch { }
            manager.Dispose();
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow(manager, options);
        window.Activate();
    }
}
