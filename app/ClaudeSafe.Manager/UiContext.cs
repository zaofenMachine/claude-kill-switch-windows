using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace ClaudeSafe.Manager;

internal sealed class UiContext(MainWindow window, ManagerService manager, Action<string, bool> status, Func<Task> refresh)
{
    public ManagerService Manager { get; } = manager;
    private bool operation;
    private bool modal;
    public bool Busy => operation || modal;
    public event Action? BusyChanged;
    public void Status(string message, bool error = false) => status(message, error);
    public async Task RunAsync(string progress, Func<Task> action, string? success = null)
    {
        if (Busy) return;
        operation = true; BusyChanged?.Invoke(); status(progress, false);
        try { await action(); if (success is not null) status(success, false); }
        catch (Exception error) { status(SafeMessage(error), true); }
        finally { operation = false; await refresh(); BusyChanged?.Invoke(); }
    }
    public async Task<bool> ConfirmAsync(string title, string message, string primary = "确认")
    {
        var dialog = new ContentDialog { XamlRoot = window.Content.XamlRoot, Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            RequestedTheme = ((FrameworkElement)window.Content).RequestedTheme,
            PrimaryButtonText = primary, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        return await PresentAsync(async () => await dialog.ShowAsync() == ContentDialogResult.Primary, false);
    }
    public async Task<string?> PickFileAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        foreach (var extension in new[] { ".yaml", ".yml", ".json" }) picker.FileTypeFilter.Add(extension);
        return await PresentAsync(async () => (await picker.PickSingleFileAsync())?.Path, (string?)null);
    }
    public async Task<(string Name, string Url)?> EntryAsync(string title, bool withName, bool withUrl, string initialName, string explanation, string submit)
    {
        var fields = new StackPanel { Spacing = 14, MinWidth = 360, MaxWidth = 480 };
        var name = new TextBox { Header = "来源名称", Text = initialName, MaxLength = 80 };
        var url = new PasswordBox { Header = "HTTPS 订阅地址", MaxLength = 8192, PasswordRevealMode = PasswordRevealMode.Peek };
        if (withName) fields.Children.Add(name);
        if (withUrl) fields.Children.Add(url);
        fields.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        fields.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = window.Content.XamlRoot, Title = title, Content = fields,
            RequestedTheme = ((FrameworkElement)window.Content).RequestedTheme,
            PrimaryButtonText = submit, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        dialog.PrimaryButtonClick += (_, e) =>
        {
            var label = name.Text.Trim(); var address = url.Password.Trim();
            if (withName && (label.Length is 0 or > 80 || label.Any(char.IsControl)))
            { error.Text = "请输入 1–80 字的名称，不含换行或控制字符。"; e.Cancel = true; return; }
            if (withUrl && (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || address.Any(char.IsWhiteSpace) || address.Any(char.IsControl)))
            { error.Text = "请输入完整 HTTPS 地址，不含空格、用户名或片段。"; e.Cancel = true; }
        };
        if (!await PresentAsync(async () => await dialog.ShowAsync() == ContentDialogResult.Primary, false)) return null;
        return (name.Text.Trim(), url.Password.Trim());
    }
    private async Task<T> PresentAsync<T>(Func<Task<T>> show, T cancelled)
    {
        if (Busy) return cancelled;
        modal = true; BusyChanged?.Invoke();
        try { return await show(); }
        catch (Exception error) { status(SafeMessage(error), true); return cancelled; }
        finally { modal = false; BusyChanged?.Invoke(); }
    }
    public static string SafeMessage(Exception error) => error is SafeOperationException ? Clean(error.Message) : "操作未完成，请检查本机配置后重试。";
    public static string Clean(string? text)
    {
        var value = (text ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= 600 ? value : value[..597] + "…";
    }
}
