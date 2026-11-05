using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ClaudeSafe.Manager;

public sealed partial class ChannelView : UserControl
{
    private UiContext? context;
    private readonly NodeListState list = new();
    private ManagerSnapshot snapshot = new();
    private bool rendering;
    public ChannelView() { InitializeComponent(); Nodes.ItemsSource = list.Rows; }
    internal void Initialize(UiContext value)
    {
        context = value;
        foreach (var name in ManagerService.PhysicalInterfaces()) Interfaces.Items.Add(name);
        UpdateActions();
    }
    internal void Render(ManagerSnapshot value)
    {
        if (snapshot.Running != value.Running || snapshot.Selected != value.Selected || snapshot.InterfaceName != value.InterfaceName) ClearProbe();
        var previousInterface = snapshot.InterfaceName; snapshot = value;
        CoreValue.Text = value.Running ? "运行中" : "已停止";
        CoreDetail.Text = value.Running ? $"独立内核 · PID {value.ProcessId}" : "启动后默认阻断";
        SelectedValue.Text = value.Selected; ToolTipService.SetToolTip(SelectedValue, value.Selected);
        SelectedDetail.Text = value.Selected == "REJECT" ? "专用通道当前拒绝连接"
            : value.AutoSwitchEnabled ? "已批准 · 定时失败后可自动切换" : "已批准 · 自动切换未启用";
        if (previousInterface != value.InterfaceName || Interfaces.SelectedItem is null)
        {
            if (!string.IsNullOrEmpty(value.InterfaceName) && !Interfaces.Items.Contains(value.InterfaceName)) Interfaces.Items.Add(value.InterfaceName);
            Interfaces.SelectedItem = value.InterfaceName;
        }
        rendering = true;
        try { list.SetNodes(value.Nodes, value.Selected); RestoreSelection(); }
        finally { rendering = false; }
        UpdateActions();
    }
    private void RestoreSelection()
    {
        Nodes.SelectedItem = list.Rows.FirstOrDefault(row => row.Name == list.SelectedName);
        Count.Text = list.CountText;
    }
    internal void UpdateActions()
    {
        var idle = context is not null && !context.Busy;
        var item = list.Selected;
        StartButton.IsEnabled = idle && !snapshot.Running;
        StopButton.IsEnabled = idle && snapshot.Running;
        RejectButton.IsEnabled = idle && snapshot.Selected != "REJECT";
        ProbeButton.IsEnabled = idle && snapshot.Running && snapshot.Selected != "REJECT";
        SelectButton.IsEnabled = idle && snapshot.Running && item?.Approved == true && item.Eligible && item.Name != snapshot.Selected;
        ApproveButton.IsEnabled = idle && item is { Approved: false, CanApprove: true };
        RevokeButton.IsEnabled = idle && item?.Approved == true;
        ImportButton.IsEnabled = idle; SaveInterfaceButton.IsEnabled = idle; Interfaces.IsEnabled = idle;
        ClearButton.IsEnabled = Search.Text.Length > 0 || ApprovedOnly.IsChecked == true;
    }
    private void OnFilterChanged(object sender, TextChangedEventArgs e) => Filter();
    private void OnApprovedChanged(object sender, RoutedEventArgs e) => Filter();
    private void Filter()
    {
        if (Nodes is null || ApprovedOnly is null || rendering) return;
        rendering = true;
        try { list.NameFilter = Search.Text; list.ApprovedOnly = ApprovedOnly.IsChecked == true; list.Refresh(); RestoreSelection(); }
        finally { rendering = false; }
        UpdateActions();
    }
    private void OnClear(object sender, RoutedEventArgs e) { Search.Text = ""; ApprovedOnly.IsChecked = false; Filter(); }
    private void OnSort(object sender, RoutedEventArgs e)
    {
        var column = Enum.Parse<NodeSortColumn>((string)((Button)sender).Tag);
        rendering = true;
        try { list.Sort(column, list.SortColumn == column && !list.Descending); RestoreSelection(); }
        finally { rendering = false; }
        foreach (var (button, key, label) in new[] { (NameHeader, NodeSortColumn.Name, "节点名称"), (ProtocolHeader, NodeSortColumn.Protocol, "协议"), (ApprovalHeader, NodeSortColumn.Approval, "批准"), (AvailabilityHeader, NodeSortColumn.Availability, "状态") })
            button.Content = label + (list.SortColumn == key ? list.Descending ? " ↓" : " ↑" : "");
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!rendering) list.SelectedName = (Nodes.SelectedItem as NodeRow)?.Name; UpdateActions(); }
    private async void OnNodeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { if (SelectButton.IsEnabled) await SelectAsync(); }
    private async void OnStart(object sender, RoutedEventArgs e) => await context!.RunAsync("正在启动专用内核…", context.Manager.StartAsync, "内核已启动，当前为 REJECT。请选择已批准节点。");
    private async void OnStop(object sender, RoutedEventArgs e) => await context!.RunAsync("正在停止专用内核…", context.Manager.StopAsync, "专用内核已停止。");
    private async void OnReject(object sender, RoutedEventArgs e) => await context!.RunAsync("正在阻断专用通道…", () => context.Manager.SelectAsync("REJECT"), "已切换 REJECT，专用通道拒绝连接。");
    private async void OnProbe(object sender, RoutedEventArgs e) => await context!.RunAsync("正在检测专用出口…", async () =>
    {
        var result = await context.Manager.ProbeAsync();
        ExitValue.Text = result.Success ? result.Country : "检查失败";
        ExitDetail.Text = result.Success ? $"{result.ElapsedMs:N0} ms · 当前通道采样" : "请检查节点或出站网卡";
        context.Status(result.Message, !result.Success);
    });
    private void ClearProbe() { ExitValue.Text = "尚未测试"; ExitDetail.Text = "匿名检测当前通道"; }
    private async void OnSelect(object sender, RoutedEventArgs e) => await SelectAsync();
    private async Task SelectAsync()
    {
        if (!SelectButton.IsEnabled || list.Selected is not { Approved: true, Eligible: true } node) return;
        await context!.RunAsync("正在切换专用节点…", () => context.Manager.SelectAsync(node.Name), "已使用所选批准节点，请检测专用出口。");
    }
    private async void OnApprove(object sender, RoutedEventArgs e)
    {
        if (list.Selected is not { } node) return;
        if (!await context!.ConfirmAsync("批准节点", $"将 {node.Name} 加入专用通道批准名单。\n\n批准只匹配当前节点身份，不保证出口地区或账号安全。", "批准节点")) return;
        await context.RunAsync("正在保存批准名单…", () => context.Manager.ApproveAsync(node.Name), "节点已批准，可手动选择使用。");
    }
    private async void OnRevoke(object sender, RoutedEventArgs e)
    {
        if (list.Selected is not { } node) return;
        await context!.RunAsync("正在移除批准…", () => context.Manager.RevokeAsync(node.Name), "已移除节点批准。");
    }
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try { if (await context!.PickFileAsync() is { } file) await context.RunAsync("正在导入本地节点来源…", () => context.Manager.ImportFileAsync(file), "已导入并使用本地来源，内核保持停止。请审阅节点后重新启动。"); }
        catch (Exception error) { context!.Status(UiContext.SafeMessage(error), true); }
    }
    private async void OnSaveInterface(object sender, RoutedEventArgs e)
    {
        if (Interfaces.SelectedItem is not string name || name.Length == 0) { context!.Status("请先选择实际使用的物理网卡。"); return; }
        await context!.RunAsync("正在保存出站网卡…", () => context.Manager.SetInterfaceAsync(name), "出站网卡已保存，请重新检测专用出口。");
    }
}
