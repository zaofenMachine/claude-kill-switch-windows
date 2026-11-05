using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ClaudeSafe.Manager;

public sealed partial class MainWindow : Window
{
    private readonly ManagerService manager;
    private readonly LaunchOptions options;
    private readonly UiContext context;
    private readonly DispatcherQueueTimer timer;
    private readonly NativeTray tray;
    private bool refreshing;
    private bool exiting;
    private bool closed;
    private bool initialized;
    private bool firstTrayTip = true;
    private string lastServiceMessage = "";

    internal MainWindow(ManagerService manager, LaunchOptions options)
    {
        this.manager = manager; this.options = options;
        InitializeComponent();
        Title = "ClaudeSafe · 专用通道管理器";
        var scale = NativeTray.DpiScale(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var desiredWidth = (int)(1240 * scale); var desiredHeight = (int)(860 * scale);
        AppWindow.Resize(new SizeInt32(desiredWidth, desiredHeight));
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (display is not null)
        {
            var area = display.WorkArea;
            var width = Math.Min(desiredWidth, area.Width - 24); var height = Math.Min(desiredHeight, area.Height - 24);
            AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        }
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBar);
            AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
            Root.Background = new SolidColorBrush(Colors.Transparent);
        }
        context = new UiContext(this, manager, SetStatus, RefreshAsync);
        Channel.Initialize(context); Subscriptions.Initialize(context); Benchmarks.Initialize(context);
        context.BusyChanged += UpdateActions;
        tray = new NativeTray(this, Navigate, ExitAsync, () => { exiting = true; manager.Dispose(); });
        AppWindow.Closing += (_, e) =>
        {
            if (exiting || tray.SystemShutdown) return;
            e.Cancel = true;
            AppWindow.Hide();
            if (firstTrayTip) { firstTrayTip = false; tray.ShowTip(); }
        };
        Closed += (_, _) => { closed = true; timer?.Stop(); tray.Dispose(); manager.Dispose(); };
        Root.ActualThemeChanged += (_, _) => UpdateTitleBar();
        Root.Loaded += OnLoaded;
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += async (_, _) => await RefreshAsync();
        Navigation.SelectedItem = ChannelItem;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;
        Navigate(options.Page);
        await RefreshAsync();
        UpdateTitleBar();
        timer.Start();
        if (options.RestoreNode is { } encoded)
            await context.RunAsync("正在恢复此前批准的节点…", () => ManagerStartupRequest.RestoreAsync(manager, encoded), "已恢复此前批准的节点，请重新检测专用出口。");
        if (options.Screenshot is { } path)
        {
            try { await Task.Delay(1200); await SaveScreenshotAsync(path); }
            catch (Exception error) { SetStatus(UiContext.SafeMessage(error), true); }
            if (options.ExitAfterScreenshot) await ExitAsync();
        }
    }

    internal void Navigate(string page)
    {
        if (closed || exiting) return;
        AppWindow.Show(); Activate();
        Navigation.SelectedItem = page switch { "subscriptions" => SubscriptionsItem, "benchmarks" => BenchmarksItem, "settings" => Navigation.SettingsItem, _ => ChannelItem };
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (Channel is null) return;
        var page = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "channel";
        Channel.Visibility = page == "channel" ? Visibility.Visible : Visibility.Collapsed;
        Subscriptions.Visibility = page == "subscriptions" ? Visibility.Visible : Visibility.Collapsed;
        Benchmarks.Visibility = page == "benchmarks" ? Visibility.Visible : Visibility.Collapsed;
        Settings.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "subscriptions" => ("订阅管理", "保存多个来源，更新缓存，审阅后切换使用。"),
            "benchmarks" => ("Desktop 测速", "检查实际资源与匿名 API 入口，查看每一轮的结果。"),
            "settings" => ("设置", "调整界面外观，了解专用通道的运行方式。"),
            _ => ("专用通道", "管理 Claude 的独立连接，选择你已批准的节点。")
        };
    }

    private async Task RefreshAsync()
    {
        if (refreshing || closed || exiting || context.Busy) return;
        refreshing = true;
        try
        {
            var state = await manager.SnapshotAsync();
            var sources = await manager.SubscriptionsAsync();
            var benchmarks = await manager.BenchmarkSnapshotAsync();
            if (closed || exiting) return;
            Channel.Render(state); Subscriptions.Render(sources); Benchmarks.Render(benchmarks);
            TrayState.Text = state.Running ? "专用内核运行中" : "内核已停止";
            tray.Update(state.Running);
            if (lastServiceMessage != state.Message)
            {
                lastServiceMessage = state.Message;
                if (!string.IsNullOrWhiteSpace(state.Message)) SetStatus(state.Message, false);
            }
        }
        catch (Exception error) { SetStatus(UiContext.SafeMessage(error), true); }
        finally { refreshing = false; UpdateActions(); }
    }

    private void UpdateActions() { Channel.UpdateActions(); Subscriptions.UpdateActions(); Benchmarks.UpdateActions(); }
    internal void SetStatus(string text, bool error)
    {
        if (closed) return;
        Status.Message = UiContext.Clean(text); Status.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
    }
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Root is null || ThemeChoice is null) return;
        Root.RequestedTheme = ThemeChoice.SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        UpdateTitleBar();
    }
    private void UpdateTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        AppWindow.TitleBar.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
    }
    private async void OnExitClicked(object sender, RoutedEventArgs e) => await ExitAsync();
    internal async Task ExitAsync()
    {
        if (closed || exiting) return;
        if (context.Busy || refreshing) { Navigate("channel"); SetStatus("当前操作完成后可退出。", false); return; }
        exiting = true; timer.Stop();
        try { await manager.StopAsync(); manager.Dispose(); tray.Dispose(); Close(); }
        catch (Exception error) { exiting = false; Navigate("channel"); SetStatus(UiContext.SafeMessage(error), true); timer.Start(); }
    }

    private async Task SaveScreenshotAsync(string path)
    {
        var image = new RenderTargetBitmap(); await image.RenderAsync(Root);
        var pixels = await image.GetPixelsAsync();
        var fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(fullPath)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(fullPath), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)image.PixelWidth, (uint)image.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }
}
