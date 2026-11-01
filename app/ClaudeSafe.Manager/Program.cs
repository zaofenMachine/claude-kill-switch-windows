using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ClaudeSafe.Manager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeSafe", "Manager");
        string? screenshot = null;
        string? benchmarkScreenshot = null;
        string? restoreNode = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data" && i + 1 < args.Length) data = args[++i];
            else if (args[i] == "--screenshot" && i + 1 < args.Length) screenshot = args[++i];
            else if (args[i] == "--benchmark-screenshot" && i + 1 < args.Length) benchmarkScreenshot = args[++i];
            else if (args[i] == "--restore-node-base64" && i + 1 < args.Length) restoreNode = args[++i];
        }

        try
        {
            data = Path.GetFullPath(data);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..20];
            using var instance = new Mutex(true, @"Local\ClaudeSafe.Manager." + key, out var created);
            if (!created)
            {
                MessageBox.Show("此配置目录的 ClaudeSafe 管理窗口已经运行。请从系统托盘打开。", "ClaudeSafe", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var manager = new ManagerService(data);
            if (benchmarkScreenshot is not null)
            {
                using var preview = new DesktopBenchmarkForm(manager);
                var file = Path.GetFullPath(benchmarkScreenshot);
                preview.Shown += async (_, _) =>
                {
                    await Task.Delay(1000);
                    using var bitmap = new System.Drawing.Bitmap(preview.Width, preview.Height);
                    preview.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, preview.Width, preview.Height));
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                    preview.Close();
                };
                Application.Run(preview);
                return;
            }
            var form = new MainForm(manager, screenshot);
            if (restoreNode is not null)
            {
                form.Shown += async (_, _) =>
                {
                    try { await ManagerStartupRequest.RestoreAsync(manager, restoreNode); }
                    catch (SafeOperationException e)
                    { if (!form.IsDisposed) MessageBox.Show(form, e.Message, "更新后恢复", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                };
            }
            Application.Run(form);
        }
        catch (Exception)
        {
            MessageBox.Show("管理器无法启动。请检查本地数据目录是否可读写，或查看本机诊断记录。", "ClaudeSafe", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
