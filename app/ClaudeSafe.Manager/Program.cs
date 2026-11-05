using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;

namespace ClaudeSafe.Manager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var options = LaunchOptions.Parse(args);
        try
        {
            var data = Path.GetFullPath(options.DataDirectory);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..20];
            using var instance = new Mutex(true, @"Local\ClaudeSafe.Manager." + key, out var created);
            if (!created)
            {
                MessageBox(IntPtr.Zero, "此配置目录的 ClaudeSafe 已经运行。请从系统托盘打开。", "ClaudeSafe", 0x40);
                return;
            }
            // The original ownership lifetime survives the UI migration: disposal stops
            // the owned core; forced termination closes Windows' kill-on-close Job.
            using var manager = new ManagerService(data);
            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                new App(manager, options);
            });
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            if (options.DiagnosticDirectory is { } directory)
                try { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "startup-error.txt"), error.ToString()); } catch { }
            MessageBox(IntPtr.Zero, "管理器无法启动。请检查程序文件和本地数据目录是否完整。", "ClaudeSafe", 0x10);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr window, string text, string title, uint type);
}

internal sealed class LaunchOptions
{
    public string DataDirectory { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeSafe", "Manager");
    public string? RestoreNode { get; private set; }
    public string Page { get; private set; } = "channel";
    public string? Screenshot { get; private set; }
    public bool ExitAfterScreenshot { get; private set; }
    public string? DiagnosticDirectory { get; private set; }

    public static LaunchOptions Parse(string[] args)
    {
        var result = new LaunchOptions();
        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) continue;
            switch (args[i])
            {
                case "--data": result.DataDirectory = args[++i]; break;
                case "--restore-node-base64": result.RestoreNode = args[++i]; break;
                case "--screenshot": result.Screenshot = args[++i]; break;
                case "--benchmark-screenshot": result.Page = "benchmarks"; result.Screenshot = args[++i]; result.ExitAfterScreenshot = true; break;
                case "--subscriptions-screenshot": result.Page = "subscriptions"; result.Screenshot = args[++i]; result.ExitAfterScreenshot = true; break;
                case "--diagnostics": result.DiagnosticDirectory = args[++i]; break;
            }
        }
        return result;
    }
}
