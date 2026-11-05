using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeSafe.Manager;

public sealed record RankRow(int Order, string Name, string Result, string Targets, string Slots, string Skipped, string Worst, string Mean, string Slowest);
public sealed record SampleRow(string Title, string Duration, string Metrics, string Result, string Detail);

public sealed partial class BenchmarksView : UserControl
{
    private UiContext? context;
    private BenchmarkManagerSnapshot? snapshot;
    private bool loaded, applying, dirty, rendering;
    private string renderedKey = "";
    private string selectedNode = "";
    private readonly ObservableCollection<RankRow> ranks = [];
    private readonly ObservableCollection<SampleRow> samples = [];
    public BenchmarksView() { InitializeComponent(); Rankings.ItemsSource = ranks; SampleNode.ItemsSource = ranks; Samples.ItemsSource = samples; }
    internal void Initialize(UiContext value) { context = value; UpdateActions(); }
    internal void Render(BenchmarkManagerSnapshot value)
    {
        snapshot = value;
        RunMessage.Text = UiContext.Clean(value.Message);
        if (!loaded || !dirty) ApplyPreferences(value.Preferences);
        loaded = true;
        ScheduleState.Text = value.Preferences.ScheduleEnabled ? $"下次定时：{(value.NextDueUtc.HasValue ? LocalTime(value.NextDueUtc.Value) : "等待调度")} · 每 {value.Preferences.IntervalHours:0.#} 小时" : "定时测试已关闭";
        var run = value.Run;
        if (run is null)
        {
            RunState.Text = "尚无测速记录 · 选择全部批准节点或当前节点开始";
            RunOptions.Text = "手动测速使用独立通道，不修改当前节点。"; Progress.Value = 0;
        }
        else
        {
            RunState.Text = $"{StatusText(run.Status)} · {(run.Trigger == "scheduled" ? "定时" : "手动")} · {run.Completed}/{run.Total} · 实测 {run.Attempted} / 跳过 {run.Skipped}";
            if (run.Targets.Any(DesktopBenchmarkTargets.IsLegacyHomepage)) RunState.Text = "旧方案（含网页首页） · " + RunState.Text;
            var protocol = run.Options.Protocol == "both" ? "双协议" : run.Options.Protocol == "h2" ? "HTTP/2" : "HTTP/1.1";
            RunOptions.Text = $"本次记录：{run.Options.Rounds} 轮 / 超时 {run.Options.TimeoutSeconds}s / {protocol} / 并发 {run.Options.Concurrency} · 首次失败跳过：{(run.Options.StopOnFirstFailure ? "开" : "关")} · {(run.FinishedUtc.HasValue ? "完成 " + LocalTime(run.FinishedUtc.Value) : "开始 " + LocalTime(run.StartedUtc))}";
            Progress.Value = run.Total > 0 ? Math.Clamp((double)run.Completed / run.Total * 100, 0, 100) : 0;
            ToolTipService.SetToolTip(RunState, UiContext.Clean(run.Message));
        }
        AutoState.Text = "自动切换：" + (value.Preferences.AutoSwitchEnabled ? "开启" : "关闭") +
            (string.IsNullOrWhiteSpace(run?.AutoSwitchOutcome) ? " · 手动测试始终不切换" : " · 本次结果：" + UiContext.Clean(run.AutoSwitchOutcome));
        var key = run is null ? "none" : $"{run.Id}|{run.Status}|{run.Completed}|{run.FinishedUtc}|{run.Message}|{run.AutoSwitchOutcome}";
        if (key != renderedKey) { renderedKey = key; RenderResults(); }
        UpdateActions();
    }
    private void RenderResults()
    {
        var run = snapshot?.Run;
        rendering = true;
        try
        {
            ranks.Clear();
            if (run is not null)
            {
                var byName = run.Ranks.ToDictionary(rank => rank.Name, StringComparer.Ordinal);
                var names = run.Ranks.Select(rank => rank.Name).Concat(run.NodeNames).Distinct(StringComparer.Ordinal).ToList();
                foreach (var name in names)
                {
                    byName.TryGetValue(name, out var rank);
                    var state = rank?.AllPassed == true ? "全通过" : run.Samples.Any(sample => sample.Name == name) ? "未全通过" : "待测";
                    ranks.Add(new RankRow(ranks.Count + 1, name, state, rank is null ? "—" : $"{rank.PassedTargets}/{rank.TotalTargets}",
                        rank is null ? "—" : $"{rank.PassedSlots}/{rank.ExpectedSlots}", rank?.SkippedSlots.ToString() ?? "—", Ms(rank?.WorstMedianMs), Ms(rank?.MeanMedianMs), Ms(rank?.SlowestMs)));
                }
            }
            var selected = ranks.FirstOrDefault(rank => rank.Name == selectedNode) ?? ranks.FirstOrDefault();
            selectedNode = selected?.Name ?? "";
            Rankings.SelectedItem = selected; SampleNode.SelectedItem = selected;
        }
        finally { rendering = false; }
        RenderSamples();
    }
    private void OnRankChanged(object sender, SelectionChangedEventArgs e)
    { if (!rendering && Rankings.SelectedItem is RankRow rank) SelectSamples(rank); }
    private void OnSampleNodeChanged(object sender, SelectionChangedEventArgs e)
    { if (!rendering && SampleNode.SelectedItem is RankRow rank) SelectSamples(rank); }
    private void SelectSamples(RankRow rank)
    {
        rendering = true;
        try { selectedNode = rank.Name; Rankings.SelectedItem = rank; SampleNode.SelectedItem = rank; }
        finally { rendering = false; }
        RenderSamples();
    }
    private void RenderSamples()
    {
        samples.Clear(); SampleHeading.Text = "选择节点查看逐轮结果";
        if (snapshot?.Run is not { } run || selectedNode.Length == 0) return;
        var nodeSamples = run.Samples.Where(sample => sample.Name == selectedNode).ToList();
        SampleHeading.Text = $"{selectedNode} · 实测 {nodeSamples.Count(sample => sample.Status != "skipped")} / 跳过 {nodeSamples.Count(sample => sample.Status == "skipped")}";
        var targets = run.Targets.ToDictionary(target => target.Id, StringComparer.Ordinal);
        var order = run.Targets.Select((target, index) => (target.Id, index)).ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        foreach (var sample in nodeSamples.OrderBy(sample => sample.Round).ThenBy(sample => order.GetValueOrDefault(sample.TargetId, int.MaxValue)).ThenBy(sample => sample.Protocol, StringComparer.Ordinal))
        {
            targets.TryGetValue(sample.TargetId, out var target);
            var skipped = DesktopBenchmarkSamples.IsSkipped(sample);
            var passed = target is not null && DesktopBenchmarkRanker.IsPassing(sample, target);
            var reason = skipped ? "已跳过 · " + (string.IsNullOrWhiteSpace(sample.Error) ? DesktopBenchmarkSamples.SkippedReason : sample.Error)
                : !string.IsNullOrWhiteSpace(sample.Error) ? sample.Error : passed ? "通过" : FailureReason(sample, target);
            var protocol = sample.Protocol == "h2" ? "HTTP/2" : sample.Protocol == "http/1.1" ? "HTTP/1.1" : sample.Protocol;
            var metrics = skipped ? "本项未发送请求" : $"HTTP {(sample.Http > 0 ? sample.Http.ToString() : "—")} · 完整：{(sample.Complete ? "是" : "否")} · {sample.ContentType} · 接收 {sample.Bytes:N0} / 预期 {(sample.ExpectedBytes.HasValue ? sample.ExpectedBytes.Value.ToString("N0") : "未知")} 字节 · TTFB {Ms(sample.TtfbMs)} ms";
            var detail = $"{target?.Url ?? sample.TargetId}\n实际协议：{sample.ActualProtocol}\nTLS 验证：{(skipped ? "未测试" : sample.TlsVerified ? "是" : "否")}\n预期类型：{target?.ExpectedType ?? "入口响应"}\n{UiContext.Clean(reason)}";
            samples.Add(new SampleRow($"第 {sample.Round} 轮 · {target?.Label ?? sample.TargetId} · {protocol}", skipped ? "—" : Ms(sample.TotalMs) + " ms", metrics, UiContext.Clean(reason), detail));
        }
    }
    private void ApplyPreferences(BenchmarkPreferences value)
    {
        applying = true;
        try
        {
            Rounds.Value = value.Rounds; Timeout.Value = value.TimeoutSeconds; Concurrency.Value = value.Concurrency;
            Hours.Value = value.IntervalHours; Protocol.SelectedIndex = value.Protocol == "h2" ? 1 : value.Protocol == "both" ? 2 : 0;
            Schedule.IsChecked = value.ScheduleEnabled; AutoSwitch.IsChecked = value.AutoSwitchEnabled; StopOnFailure.IsChecked = value.StopOnFirstFailure; dirty = false;
        }
        finally { applying = false; }
    }
    private BenchmarkPreferences ReadPreferences()
    {
        if (new[] { Rounds.Value, Timeout.Value, Concurrency.Value, Hours.Value }.Any(number => !double.IsFinite(number)) ||
            Rounds.Value % 1 != 0 || Timeout.Value % 1 != 0 || Concurrency.Value % 1 != 0)
            throw new SafeOperationException("请填写有效数字；轮数、超时和并发数需为整数。");
        return new BenchmarkPreferences { Rounds = (int)Rounds.Value, TimeoutSeconds = (int)Timeout.Value, Concurrency = (int)Concurrency.Value,
            IntervalHours = Hours.Value, Protocol = Protocol.SelectedIndex == 1 ? "h2" : Protocol.SelectedIndex == 0 ? "http/1.1" : "both",
            ScheduleEnabled = Schedule.IsChecked == true, AutoSwitchEnabled = AutoSwitch.IsChecked == true, StopOnFirstFailure = StopOnFailure.IsChecked == true };
    }
    private void OnNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => PreferencesChanged();
    private void OnProtocolChanged(object sender, SelectionChangedEventArgs e) => PreferencesChanged();
    private void OnOptionChanged(object sender, RoutedEventArgs e) => PreferencesChanged();
    private void PreferencesChanged() { if (applying || !loaded) return; dirty = true; UpdateActions(); }
    internal void UpdateActions()
    {
        if (AllButton is null) return;
        var idle = context is not null && !context.Busy;
        AllButton.IsEnabled = CurrentButton.IsEnabled = idle && loaded && snapshot?.Busy != true && !dirty;
        CancelButton.IsEnabled = idle && snapshot?.Busy == true;
        SaveButton.IsEnabled = idle && loaded && dirty;
        foreach (Control control in new Control[] { Rounds, Timeout, Concurrency, Protocol, Schedule, AutoSwitch, StopOnFailure }) control.IsEnabled = idle;
        Hours.IsEnabled = idle && Schedule.IsChecked == true;
        PreferencesState.Text = !loaded ? "正在读取设置…" : dirty ? "有未保存设置，保存后可开始测速" : $"已保存 · {Rounds.Value:0} 轮 · {(Protocol.SelectedIndex == 2 ? "双协议" : Protocol.SelectedIndex == 1 ? "HTTP/2" : "HTTP/1.1")} · 自动切换{(AutoSwitch.IsChecked == true ? "开启" : "关闭")}";
        SaveButton.Content = dirty ? "保存设置 *" : "保存设置";
    }
    private async void OnSave(object sender, RoutedEventArgs e) => await context!.RunAsync("正在保存测速设置…", async () =>
    { await context.Manager.SaveBenchmarkPreferencesAsync(ReadPreferences()); dirty = false; }, "设置已保存，新参数用于下一次任务。");
    private async void OnAll(object sender, RoutedEventArgs e) => await StartAsync(false);
    private async void OnCurrent(object sender, RoutedEventArgs e) => await StartAsync(true);
    private async Task StartAsync(bool currentOnly)
    {
        if (dirty) { context!.Status("请先保存设置，再开始测速。"); return; }
        await context!.RunAsync("正在开始 Desktop 测速…", () => context.Manager.StartBenchmarkAsync(currentOnly), "测速已启动，手动任务不会自动切换节点。");
    }
    private async void OnCancel(object sender, RoutedEventArgs e) => await context!.RunAsync("正在取消本次测速…", context.Manager.CancelBenchmarkAsync, "已请求取消；保留已有样本，不会自动切换。");
    private static string LocalTime(DateTimeOffset time) => time.ToLocalTime().ToString("MM-dd HH:mm");
    private static string Ms(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 ? value.Value.ToString("N0") : "—";
    private static string StatusText(string value) => value switch
    { "preparing" => "准备中", "running" => "测速中", "completed" => "已完成", "cancelled" or "canceled" or "stopped" => "已取消", "cancelling" or "canceling" => "正在取消", "error" or "failed" or "interrupted" => "测试中断", "idle" => "就绪", _ => value };
    private static string FailureReason(BenchmarkSample sample, BenchmarkTarget? target)
    {
        if (target?.Kind == "resource" && sample.Http is 404 or 410) return "资源已失效，需更新测试项";
        if (!sample.Complete) return "响应未完整接收";
        if (!sample.TlsVerified) return "TLS 未通过验证";
        if (sample.ExpectedBytes.HasValue && sample.ExpectedBytes != sample.Bytes) return "接收字节与预期不符";
        if (target?.Kind == "resource" && !sample.ResourceValid) return "内容类型或状态不符";
        if (sample.Http is >= 300 and < 400) return "重定向，未跟随";
        if (sample.Http == 429) return "请求受限";
        if (sample.Http is 401 or 403) return "需要认证或访问被拒绝";
        if (sample.Http >= 500) return "服务端错误";
        return string.IsNullOrEmpty(sample.Status) ? "未通过" : UiContext.Clean(sample.Status);
    }
}
