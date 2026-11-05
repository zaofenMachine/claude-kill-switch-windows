using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeSafe.Manager;

public sealed record SubscriptionRow(SubscriptionSummary Source)
{
    public string Id => Source.Id;
    public string Name => UiContext.Clean(Source.Name);
    public string SourceDescription => Source.Kind == "https" ? "HTTPS 订阅 · " + UiContext.Clean(Source.MaskedUrl) : "本地文件快照";
    public override string ToString() => Name;
    public string ActiveLabel => Source.Active ? "使用中" : "未使用";
    public string NodeCountLabel => $"{Source.NodeCount} 个节点";
    public string ResultLabel => Source.LastError.Length > 0 ? "更新失败 · 已保留缓存" : Source.LastUpdatedUtc.HasValue ? "已缓存" : "尚无更新记录";
    public string UpdatedLabel => Source.LastUpdatedUtc?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—";
}

public sealed partial class SubscriptionsView : UserControl
{
    private UiContext? context;
    private SubscriptionSnapshot? snapshot;
    private readonly ObservableCollection<SubscriptionRow> rows = [];
    private string renderedKey = "";
    private bool rendering;
    public SubscriptionsView() { InitializeComponent(); Sources.ItemsSource = rows; }
    internal void Initialize(UiContext value) { context = value; UpdateActions(); }
    private SubscriptionSummary? Selected => (Sources.SelectedItem as SubscriptionRow)?.Source;
    internal void Render(SubscriptionSnapshot value)
    {
        snapshot = value;
        var active = value.Items.FirstOrDefault(item => item.Active);
        Current.Text = active is null ? $"当前没有使用的来源 · 已保存 {value.Items.Count} 份" : $"当前来源：{UiContext.Clean(active.Name)} · {active.NodeCount} 个节点 · 已保存 {value.Items.Count} 份";
        var key = System.Text.Json.JsonSerializer.Serialize(value.Items);
        if (key != renderedKey)
        {
            var keep = Selected?.Id;
            rendering = true;
            try
            {
                rows.Clear(); foreach (var item in value.Items) rows.Add(new SubscriptionRow(item));
                Sources.SelectedItem = rows.FirstOrDefault(item => item.Id == keep) ?? rows.FirstOrDefault(item => item.Source.Active) ?? rows.FirstOrDefault();
                renderedKey = key;
            }
            finally { rendering = false; }
        }
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderSelection(); UpdateActions();
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) { if (rendering) return; RenderSelection(); UpdateActions(); }
    private void RenderSelection()
    {
        var item = Selected;
        SelectionHint.Text = item is null ? "选择来源后，可更新订阅或切换使用。" : item.LastError.Length > 0 ? $"{UiContext.Clean(item.Name)}：{UiContext.Clean(item.LastError)}"
            : item.Active ? "这是当前使用的来源。更新内容不变时连接保留；节点内容变化时停止内核，等待重新启动。"
            : "此来源尚未使用。点击“使用所选来源”后，审阅节点并重新启动内核。";
    }
    internal void UpdateActions()
    {
        var idle = context is not null && !context.Busy && snapshot is { Busy: false }; var item = Selected;
        AddButton.IsEnabled = ImportButton.IsEnabled = idle;
        UpdateButton.IsEnabled = ReplaceButton.IsEnabled = idle && item?.Kind == "https";
        UpdateAllButton.IsEnabled = idle && snapshot!.Items.Any(source => source.Kind == "https");
        UseButton.IsEnabled = idle && item is { Active: false };
        RenameButton.IsEnabled = RemoveButton.IsEnabled = idle && item is not null;
    }
    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var entry = await context!.EntryAsync("添加 HTTPS 订阅", true, true, "", "下载成功后保存为新来源，暂不切换当前通道。", "下载并保存");
        if (entry is { } value) await context.RunAsync("正在下载并保存新订阅…", () => context.Manager.AddSubscriptionAsync(value.Name, value.Url), "订阅已保存，可选择使用。");
    }
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try { if (await context!.PickFileAsync() is { } file) await context.RunAsync("正在导入本地来源…", () => context.Manager.ImportFileAsync(file), "已导入并使用本地来源，内核保持停止。"); }
        catch (Exception error) { context!.Status(UiContext.SafeMessage(error), true); }
    }
    private async void OnUpdate(object sender, RoutedEventArgs e)
    { if (Selected is { Kind: "https" } item) await context!.RunAsync("正在更新所选订阅…", () => context.Manager.UpdateSubscriptionAsync(item.Id), "订阅更新操作完成，请检查来源状态。"); }
    private async void OnUpdateAll(object sender, RoutedEventArgs e) => await context!.RunAsync("正在更新全部 HTTPS 订阅…", context.Manager.UpdateAllSubscriptionsAsync, "更新操作完成，请检查各来源的更新结果。");
    private async void OnUse(object sender, RoutedEventArgs e)
    { if (Selected is { Active: false } item) await context!.RunAsync("正在切换节点来源…", () => context.Manager.UseSubscriptionAsync(item.Id), "已切换来源，内核保持停止。请审阅节点后重新启动。"); }
    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        var entry = await context!.EntryAsync("重命名来源", true, false, item.Name, "仅修改显示名称，保留节点缓存和当前连接。", "保存名称");
        if (entry is { } value) await context.RunAsync("正在保存名称…", () => context.Manager.RenameSubscriptionAsync(item.Id, value.Name), "来源名称已保存。");
    }
    private async void OnReplace(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Kind: "https" } item) return;
        var entry = await context!.EntryAsync("更换订阅地址", false, true, "", "保留现有缓存和当前连接。保存后需再点击更新。", "保存地址");
        if (entry is { } value) await context.RunAsync("正在保存订阅地址…", () => context.Manager.SetSubscriptionUrlAsync(item.Id, value.Url), "新地址已保存，请更新订阅缓存。");
    }
    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        var consequence = item.Active ? "这是正在使用的来源。删除后会停止专用内核，并清空当前节点来源。" : "将删除此来源的本地缓存和保存的地址，当前连接保持不变。";
        if (await context!.ConfirmAsync("删除来源", $"{UiContext.Clean(item.Name)}\n\n{consequence}", "删除来源"))
            await context.RunAsync("正在删除来源…", () => context.Manager.RemoveSubscriptionAsync(item.Id), "来源已删除。");
    }
}
