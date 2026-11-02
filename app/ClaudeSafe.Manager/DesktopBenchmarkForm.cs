using System.Drawing;
using System.Globalization;

namespace ClaudeSafe.Manager;

/// <summary>The window observes the service; closing it never stops its scheduled work.</summary>
public sealed class DesktopBenchmarkForm : Form
{
    private static readonly Color Navy = Color.FromArgb(18, 35, 57);
    private static readonly Color Ink = Color.FromArgb(28, 44, 63);
    private static readonly Color Muted = Color.FromArgb(99, 116, 135);
    private static readonly Color Blue = Color.FromArgb(35, 97, 213);
    private static readonly Color Green = Color.FromArgb(25, 127, 92);
    private static readonly Color Red = Color.FromArgb(173, 55, 65);
    private static readonly Color Canvas = Color.FromArgb(242, 245, 249);
    private static readonly Color Border = Color.FromArgb(220, 227, 235);
    private readonly ManagerService manager;
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1000 };
    private readonly NumericUpDown rounds = Number(1, 10, 3);
    private readonly NumericUpDown timeout = Number(3, 60, 30);
    private readonly NumericUpDown concurrency = Number(1, 4, 2);
    private readonly NumericUpDown hours = Number(.1M, 8760, 24, 1);
    private readonly ComboBox protocol = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 122 };
    private readonly CheckBox schedule = new() { Text = "定时测试", Checked = true, AutoSize = true };
    private readonly CheckBox autoSwitch = new() { Text = "当前节点不全过时，自动切换最佳节点", Checked = false, AutoSize = true };
    private readonly CheckBox stopOnFirstFailure = new() { Text = "节点首次失败后跳过后续测试（默认关闭）", Checked = false, AutoSize = true };
    private readonly Button save = ActionButton("保存设置", true);
    private readonly Button testAll = ActionButton("测速全部批准节点", true, 155);
    private readonly Button testCurrent = ActionButton("仅测当前节点", false, 135);
    private readonly Button cancel = ActionButton("取消本次测速", false, 135);
    private readonly Label dirtyLabel = Detail("正在读取设置…");
    private readonly Label runLabel = Detail("尚无测速记录");
    private readonly Label runOptionsLabel = Detail("记录参数：尚无测速记录");
    private readonly Label scheduleLabel = Detail("正在读取定时状态…");
    private readonly Label autoLabel = Detail("自动切换默认关闭");
    private readonly Label notice = Detail("测速使用独立通道，不修改当前节点；手动测速不会自动切换。");
    private readonly Label sampleHeading = Title("样本明细");
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000 };
    private readonly DataGridView rankings = Grid();
    private readonly DataGridView samples = Grid();
    private readonly ToolTip hints = new() { AutoPopDelay = 15000 };
    private bool operation;
    private bool refreshing;
    private bool loaded;
    private bool applyingPreferences;
    private bool dirty;
    private bool benchmarkBusy;
    private bool rendering;
    private BenchmarkRun? currentRun;
    private string renderedRunKey = "";
    private string selectedRankNode = "";

    public DesktopBenchmarkForm(ManagerService manager)
    {
        this.manager = manager;
        Text = "ClaudeSafe · Desktop 测速与定时";
        ClientSize = new Size(1220, 880);
        MinimumSize = new Size(1080, 790);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        ForeColor = Ink;
        BackColor = Canvas;
        Icon = SystemIcons.Shield;
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildLayout();
        WireActions();
        UpdateActions();
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => { await RefreshAsync(); if (!IsDisposed) refreshTimer.Start(); };
        FormClosing += (_, _) => refreshTimer.Stop();
    }

    private void BuildLayout()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 77));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        Controls.Add(page);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Navy, Padding = new Padding(23, 11, 20, 11), Margin = Padding.Empty };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label { Text = "Desktop 实际资源测速", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 17F, FontStyle.Bold), ForeColor = Color.White, Margin = Padding.Empty }, 0, 0);
        header.Controls.Add(new Label { Text = "Desktop 实际 JS / CSS + API 连通性  ·  不测试网页首页或登录", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(184, 205, 229), Margin = Padding.Empty, AutoEllipsis = true }, 0, 1);
        page.Controls.Add(header, 0, 0);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(19, 15, 19, 8), Margin = Padding.Empty };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 172));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 109));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        page.Controls.Add(content, 0, 1);
        content.Controls.Add(SettingsPanel(), 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 7, 0, 0) };
        actions.Controls.AddRange([testAll, testCurrent, cancel]);
        var actionHint = Detail($"3 个固定资源（{DesktopBenchmarkTargets.ObservedDate}）+ API；不含网页首页。资源 404 / 410 需维护。");
        actionHint.AutoSize = true; actionHint.Margin = new Padding(9, 9, 0, 0);
        actions.Controls.Add(actionHint);
        content.Controls.Add(actions, 0, 1);

        var state = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 2, BackColor = Color.White, Padding = new Padding(11, 7, 11, 6), Margin = new Padding(0, 0, 0, 10) };
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        state.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        state.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        state.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        state.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var label in new[] { runLabel, runOptionsLabel, scheduleLabel, autoLabel }) { label.Dock = DockStyle.Fill; label.AutoEllipsis = true; label.TextAlign = ContentAlignment.MiddleLeft; }
        runLabel.ForeColor = Ink;
        state.Controls.Add(runLabel, 0, 0); state.Controls.Add(scheduleLabel, 1, 0);
        state.Controls.Add(runOptionsLabel, 0, 1); state.SetColumnSpan(runOptionsLabel, 2);
        state.Controls.Add(autoLabel, 0, 2); state.SetColumnSpan(autoLabel, 2);
        state.Controls.Add(progress, 0, 3); state.SetColumnSpan(progress, 2);
        content.Controls.Add(state, 0, 2);

        var split = new SplitContainer { Size = new Size(1100, 420), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 195, Margin = Padding.Empty, SplitterWidth = 8, Panel1MinSize = 130, Panel2MinSize = 125, BackColor = Canvas };
        // Set after layout so high DPI and minimum-size windows retain two usable tables.
        split.SizeChanged += (_, _) =>
        {
            if (split.Height > 300 && split.SplitterDistance < 140)
                split.SplitterDistance = Math.Max(140, (int)(split.Height * .48));
        };
        var rankPanel = TablePanel("节点排名 · 全通过优先，再比较最差目标中位数");
        ConfigureRankings(); rankPanel.Controls.Add(rankings, 0, 1); split.Panel1.Controls.Add(rankPanel);
        var samplePanel = TablePanel("");
        samplePanel.Controls.Remove(samplePanel.GetControlFromPosition(0, 0)!);
        sampleHeading.Dock = DockStyle.Fill;
        samplePanel.Controls.Add(sampleHeading, 0, 0);
        ConfigureSamples(); samplePanel.Controls.Add(samples, 0, 1); split.Panel2.Controls.Add(samplePanel);
        content.Controls.Add(split, 0, 3);
        Shown += (_, _) => { if (split.Height > 300) split.SplitterDistance = (int)(split.Height * .47); };

        notice.Dock = DockStyle.Fill; notice.TextAlign = ContentAlignment.MiddleLeft; notice.AutoEllipsis = true;
        content.Controls.Add(notice, 0, 4);
        var footer = Detail("关闭此窗口不取消后台测试；主窗口关闭到托盘仍可定时测试，退出管理器后不再运行。匿名测速不代表登录或账号安全。");
        footer.Dock = DockStyle.Fill; footer.TextAlign = ContentAlignment.MiddleCenter; footer.AutoEllipsis = true;
        footer.BackColor = Color.FromArgb(231, 237, 244);
        page.Controls.Add(footer, 0, 2);
    }

    private Control SettingsPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(12, 9, 12, 7), BackColor = Color.White, Margin = Padding.Empty };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        heading.Controls.Add(Title("测速与调度设置"), 0, 0);
        dirtyLabel.Dock = DockStyle.Fill; dirtyLabel.TextAlign = ContentAlignment.MiddleRight;
        heading.Controls.Add(dirtyLabel, 1, 0); panel.Controls.Add(heading, 0, 0);

        protocol.Items.AddRange(["HTTP/1.1", "HTTP/2", "两者"]); protocol.SelectedIndex = 2;
        var testOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        AddField(testOptions, "轮数", rounds);
        AddField(testOptions, "单项超时 / 秒", timeout);
        AddField(testOptions, "并发节点", concurrency);
        AddField(testOptions, "协议", protocol);
        panel.Controls.Add(testOptions, 0, 1);

        var failureOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        stopOnFirstFailure.Margin = new Padding(0, 3, 15, 0);
        failureOptions.Controls.Add(stopOnFirstFailure);
        var failureHint = Detail("仅跳过失败节点的剩余项目和轮次，其他节点继续。");
        failureHint.AutoSize = true; failureHint.Margin = new Padding(0, 5, 0, 0);
        failureOptions.Controls.Add(failureHint);
        panel.Controls.Add(failureOptions, 0, 2);

        var scheduleOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        schedule.Margin = new Padding(0, 5, 18, 0);
        scheduleOptions.Controls.Add(schedule);
        AddField(scheduleOptions, "间隔 / 小时", hours);
        autoSwitch.Margin = new Padding(12, 5, 14, 0);
        scheduleOptions.Controls.Add(autoSwitch);
        save.Size = new Size(117, 29); save.Margin = Padding.Empty;
        scheduleOptions.Controls.Add(save);
        panel.Controls.Add(scheduleOptions, 0, 3);
        var policy = Detail("自动切换仅用于定时全量测试，默认关闭：当前节点不全过时选全通过最佳节点；没有全通过节点时阻断。修改参数应用于下一次测试。");
        policy.Dock = DockStyle.Fill; policy.AutoEllipsis = true; policy.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(policy, 0, 4);
        hints.SetToolTip(autoSwitch, "只对完整的定时全量测试生效。手动测试不切换；测试中手动改变节点或配置时跳过该次自动切换。");
        hints.SetToolTip(stopOnFirstFailure, "默认关闭。启用后，一项请求未通过即跳过同一节点后续项目、协议和轮次；跳过不会算作通过。此选项应用于下一次手动或定时测试。");
        hints.SetToolTip(hours, "管理器需要持续运行或驻留托盘。退出后不执行测试；再次运行时由服务处理到期任务。");
        return panel;
    }

    private void ConfigureRankings()
    {
        rankings.Columns.Add(Column("Order", "排名", 50));
        rankings.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "批准节点", MinimumWidth = 220, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        rankings.Columns.Add(Column("AllPassed", "全部通过", 92));
        rankings.Columns.Add(Column("Targets", "目标通过", 88));
        rankings.Columns.Add(Column("Slots", "样本通过", 94));
        rankings.Columns.Add(Column("Skipped", "跳过", 61));
        rankings.Columns.Add(Column("Worst", "最差目标中位 / ms", 151));
        rankings.Columns.Add(Column("Mean", "平均中位 / ms", 132));
        rankings.Columns.Add(Column("Slowest", "最慢样本 / ms", 132));
        rankings.SelectionChanged += (_, _) =>
        {
            if (rendering) return;
            selectedRankNode = rankings.SelectedRows.Count > 0 ? rankings.SelectedRows[0].Tag as string ?? "" : "";
            RenderSamples();
        };
        hints.SetToolTip(rankings, "全通过要求：本次每个目标 × 所选协议 × 每一轮，恰好一个有效完整样本。缺失、重复、取消或部分下载均不算通过。");
    }

    private void ConfigureSamples()
    {
        samples.MultiSelect = false;
        samples.Columns.Add(new DataGridViewTextBoxColumn { Name = "Target", HeaderText = "测试项", MinimumWidth = 165, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 100, SortMode = DataGridViewColumnSortMode.NotSortable });
        samples.Columns.Add(Column("Protocol", "协议", 79));
        samples.Columns.Add(Column("Round", "轮", 42));
        samples.Columns.Add(Column("Http", "HTTP", 55));
        samples.Columns.Add(Column("Complete", "完整", 55));
        samples.Columns.Add(Column("Type", "内容类型", 121));
        samples.Columns.Add(Column("Bytes", "接收 / 预期字节", 150));
        samples.Columns.Add(Column("Ttfb", "TTFB / ms", 88));
        samples.Columns.Add(Column("Total", "总时长 / ms", 99));
        samples.Columns.Add(new DataGridViewTextBoxColumn { Name = "Result", HeaderText = "结果 / 原因", MinimumWidth = 180, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
    }

    private void WireActions()
    {
        foreach (var number in new[] { rounds, timeout, concurrency, hours }) number.ValueChanged += (_, _) => PreferencesChanged();
        protocol.SelectedIndexChanged += (_, _) => PreferencesChanged();
        schedule.CheckedChanged += (_, _) => PreferencesChanged();
        autoSwitch.CheckedChanged += (_, _) => PreferencesChanged();
        stopOnFirstFailure.CheckedChanged += (_, _) => PreferencesChanged();
        save.Click += async (_, _) => await RunOperationAsync(async () =>
        {
            await manager.SaveBenchmarkPreferencesAsync(ReadPreferences());
            dirty = false;
            SetNotice("设置已保存。已有任务仍按它启动时的参数运行；新参数用于下一次测试。", false);
        });
        testAll.Click += async (_, _) => await StartAsync(false);
        testCurrent.Click += async (_, _) => await StartAsync(true);
        cancel.Click += async (_, _) => await RunOperationAsync(async () =>
        {
            await manager.CancelBenchmarkAsync();
            SetNotice("已请求取消；保留已有样本，取消的测试不会自动切换。", false);
        });
    }

    private async Task StartAsync(bool currentOnly)
    {
        if (dirty) { SetNotice("请先保存设置，再开始测速。", false); return; }
        await RunOperationAsync(async () =>
        {
            await manager.StartBenchmarkAsync(currentOnly);
            SetNotice(currentOnly ? "已启动当前节点的匿名 Desktop 资源测速；不会自动切换。" : "已启动全部批准节点测速；手动任务不会自动切换。", false);
        });
    }

    private async Task RunOperationAsync(Func<Task> work)
    {
        if (operation || refreshing || IsDisposed) return;
        operation = true; UpdateActions();
        try { await work(); }
        catch (Exception ex) { SetNotice(SafeMessage(ex), true); }
        finally
        {
            operation = false;
            if (!IsDisposed) { await RefreshAsync(); UpdateActions(); }
        }
    }

    private async Task RefreshAsync()
    {
        if (operation || refreshing || IsDisposed) return;
        refreshing = true; UpdateActions();
        try
        {
            var snapshot = await manager.BenchmarkSnapshotAsync();
            if (IsDisposed) return;
            benchmarkBusy = snapshot.Busy;
            if (!loaded || !dirty) ApplyPreferences(snapshot.Preferences);
            loaded = true;
            RenderState(snapshot);
        }
        catch (Exception ex) { SetNotice(SafeMessage(ex), true); }
        finally { refreshing = false; if (!IsDisposed) UpdateActions(); }
    }

    private void RenderState(BenchmarkManagerSnapshot snapshot)
    {
        currentRun = snapshot.Run;
        var run = snapshot.Run;
        var next = snapshot.NextDueUtc;
        scheduleLabel.Text = snapshot.Preferences.ScheduleEnabled
            ? $"下次定时：{(next.HasValue ? LocalTime(next.Value) : "等待调度")}  ·  每 {snapshot.Preferences.IntervalHours:0.#} 小时"
            : "定时测试：已关闭";
        if (run == null)
        {
            runLabel.Text = "尚无测速记录 · 请选择全部批准节点或当前节点";
            runOptionsLabel.Text = "记录参数：尚无测速记录";
            progress.Value = 0;
            autoLabel.Text = snapshot.Preferences.AutoSwitchEnabled
                ? "自动切换已开启，等待完整的定时全量结果。"
                : "自动切换：关闭 · 手动测速始终不切换";
            autoLabel.ForeColor = snapshot.Preferences.AutoSwitchEnabled ? Ink : Muted;
        }
        else
        {
            var last = run.FinishedUtc.HasValue ? $" · 上次完成 {LocalTime(run.FinishedUtc.Value)}" : $" · 开始 {LocalTime(run.StartedUtc)}";
            var protocols = run.Options.Protocol == "both" ? "双协议" : run.Options.Protocol == "h2" ? "HTTP/2" : "HTTP/1.1";
            runLabel.Text = $"{StatusText(run.Status)} · {(run.Trigger == "scheduled" ? "定时" : "手动")} · {run.Completed}/{run.Total} · 实测 {run.Attempted} / 跳过 {run.Skipped}";
            if (run.Targets.Any(DesktopBenchmarkTargets.IsLegacyHomepage))
                runLabel.Text = "旧方案（含网页首页） · " + runLabel.Text;
            runOptionsLabel.Text = $"记录参数：{run.Options.Rounds}轮 / 单项超时 {run.Options.TimeoutSeconds}s / {protocols} / 并发 {run.Options.Concurrency} 节点 · 首次失败跳过：{(run.Options.StopOnFirstFailure ? "开" : "关")}{last}";
            hints.SetToolTip(runLabel, $"{runLabel.Text}\n{runOptionsLabel.Text}\n{Clean(run.Message)}");
            hints.SetToolTip(runOptionsLabel, runOptionsLabel.Text);
            var ratio = run.Total > 0 ? Math.Clamp((double)run.Completed / run.Total, 0, 1) : 0;
            progress.Value = (int)(ratio * progress.Maximum);
            var autoState = snapshot.Preferences.AutoSwitchEnabled ? "开启" : "关闭";
            autoLabel.Text = "自动切换：" + autoState + (!string.IsNullOrWhiteSpace(run.AutoSwitchOutcome)
                ? " · 本次结果：" + Clean(run.AutoSwitchOutcome)
                : " · 仅完整的定时全量测试应用；手动测试不切换。");
            autoLabel.ForeColor = Muted;
            hints.SetToolTip(autoLabel, autoLabel.Text);
        }
        if (!string.IsNullOrWhiteSpace(snapshot.Message)) hints.SetToolTip(scheduleLabel, Clean(snapshot.Message));

        var key = run == null ? "none" : $"{run.Id}|{run.Status}|{run.Completed}|{run.FinishedUtc}|{run.Message}|{run.AutoSwitchOutcome}";
        if (key != renderedRunKey)
        {
            renderedRunKey = key;
            RenderRankings();
            RenderSamples();
        }
    }

    private void RenderRankings()
    {
        var keep = selectedRankNode;
        var scroll = rankings.FirstDisplayedScrollingRowIndex;
        rendering = true; rankings.SuspendLayout();
        try
        {
            rankings.Rows.Clear();
            if (currentRun == null) { selectedRankNode = ""; return; }
            var run = currentRun;
            var byName = run.Ranks.ToDictionary(rank => rank.Name, StringComparer.Ordinal);
            var names = run.Ranks.Select(rank => rank.Name).Concat(run.NodeNames).Distinct(StringComparer.Ordinal).ToList();
            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i];
                byName.TryGetValue(name, out var rank);
                var text = rank?.AllPassed == true ? "全通过" : run.Samples.Any(sample => sample.Name == name) ? "未全通过" : "待测";
                var rowIndex = rankings.Rows.Add(i + 1, name, text,
                    rank == null ? "—" : $"{rank.PassedTargets}/{rank.TotalTargets}",
                    rank == null ? "—" : $"{rank.PassedSlots}/{rank.ExpectedSlots}",
                    rank == null ? "—" : rank.SkippedSlots.ToString(CultureInfo.InvariantCulture),
                    Milliseconds(rank?.WorstMedianMs), Milliseconds(rank?.MeanMedianMs), Milliseconds(rank?.SlowestMs));
                var row = rankings.Rows[rowIndex]; row.Tag = name;
                row.Cells["AllPassed"].Style.ForeColor = rank?.AllPassed == true ? Green : text == "待测" ? Muted : Red;
                row.Cells["Name"].ToolTipText = name;
                row.Cells["Skipped"].Style.ForeColor = Muted;
                row.Cells["Skipped"].ToolTipText = "按设置跳过的请求不计入通过；该节点仍为未全通过。";
            }
            if (!names.Contains(keep, StringComparer.Ordinal)) keep = names.FirstOrDefault() ?? "";
            foreach (DataGridViewRow row in rankings.Rows) row.Selected = row.Tag as string == keep;
            selectedRankNode = keep;
            if (scroll >= 0 && scroll < rankings.Rows.Count) rankings.FirstDisplayedScrollingRowIndex = scroll;
        }
        finally { rankings.ResumeLayout(); rendering = false; }
    }

    private void RenderSamples()
    {
        var scroll = samples.FirstDisplayedScrollingRowIndex;
        samples.SuspendLayout();
        try
        {
            samples.Rows.Clear();
            sampleHeading.Text = string.IsNullOrEmpty(selectedRankNode) ? "样本明细 · 选择上方节点" : "样本明细 · " + selectedRankNode;
            if (currentRun == null || string.IsNullOrEmpty(selectedRankNode)) return;
            var nodeSamples = currentRun.Samples.Where(s => s.Name == selectedRankNode).ToList();
            sampleHeading.Text += $"  ·  实测 {nodeSamples.Count(s => s.Status != "skipped")} / 跳过 {nodeSamples.Count(s => s.Status == "skipped")}";
            var targets = currentRun.Targets.ToDictionary(target => target.Id, StringComparer.Ordinal);
            var targetOrder = currentRun.Targets.Select((target, i) => (target.Id, i)).ToDictionary(pair => pair.Id, pair => pair.i, StringComparer.Ordinal);
            foreach (var sample in nodeSamples
                .OrderBy(s => s.Round).ThenBy(s => targetOrder.GetValueOrDefault(s.TargetId, int.MaxValue)).ThenBy(s => s.Protocol, StringComparer.Ordinal))
            {
                targets.TryGetValue(sample.TargetId, out var target);
                var skipped = sample.Status == "skipped";
                var passes = SamplePassed(sample, target);
                var reason = skipped ? "已跳过 · " + (string.IsNullOrWhiteSpace(sample.Error) ? "此前测试未通过，按设置跳过后续测试。" : sample.Error)
                    : !string.IsNullOrWhiteSpace(sample.Error) ? sample.Error : passes ? "通过" : FailureReason(sample, target);
                var bytes = skipped ? "—" : $"{sample.Bytes:N0} / {(sample.ExpectedBytes.HasValue ? sample.ExpectedBytes.Value.ToString("N0", CultureInfo.CurrentCulture) : "未知")}";
                var index = samples.Rows.Add(target?.Label ?? sample.TargetId, sample.Protocol == "h2" ? "HTTP/2" : sample.Protocol == "http/1.1" ? "HTTP/1.1" : sample.Protocol,
                    sample.Round, sample.Http > 0 && !skipped ? sample.Http.ToString(CultureInfo.InvariantCulture) : "—", skipped ? "—" : sample.Complete ? "是" : "否", string.IsNullOrEmpty(sample.ContentType) || skipped ? "—" : sample.ContentType,
                    bytes, skipped ? "—" : Milliseconds(sample.TtfbMs), skipped ? "—" : Milliseconds(sample.TotalMs), Clean(reason));
                var row = samples.Rows[index];
                if (skipped) row.DefaultCellStyle.ForeColor = Muted;
                row.Cells["Complete"].Style.ForeColor = skipped ? Muted : sample.Complete ? Green : Red;
                row.Cells["Result"].Style.ForeColor = skipped ? Muted : passes ? Green : Red;
                foreach (DataGridViewCell cell in row.Cells)
                    cell.ToolTipText = $"{target?.Url ?? sample.TargetId}\nTLS 验证：{(skipped ? "未测试" : sample.TlsVerified ? "是" : "否")}\n预期类型：{target?.ExpectedType ?? "入口响应"}\n{Clean(reason)}";
            }
            if (scroll >= 0 && scroll < samples.Rows.Count) samples.FirstDisplayedScrollingRowIndex = scroll;
        }
        finally { samples.ResumeLayout(); }
    }

    private static bool SamplePassed(BenchmarkSample sample, BenchmarkTarget? target) =>
        target != null && DesktopBenchmarkRanker.IsPassing(sample, target);

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
        return string.IsNullOrEmpty(sample.Status) ? "未通过" : Clean(sample.Status);
    }

    private void ApplyPreferences(BenchmarkPreferences value)
    {
        applyingPreferences = true;
        try
        {
            rounds.Value = Math.Clamp(value.Rounds, 1, 10);
            timeout.Value = Math.Clamp(value.TimeoutSeconds, 3, 60);
            concurrency.Value = Math.Clamp(value.Concurrency, 1, 4);
            hours.Value = double.IsFinite(value.IntervalHours) ? (decimal)Math.Clamp(value.IntervalHours, .1, 8760) : 24;
            protocol.SelectedIndex = value.Protocol == "h2" ? 1 : value.Protocol == "both" ? 2 : 0;
            schedule.Checked = value.ScheduleEnabled;
            autoSwitch.Checked = value.AutoSwitchEnabled;
            stopOnFirstFailure.Checked = value.StopOnFirstFailure;
            dirty = false;
        }
        finally { applyingPreferences = false; }
    }

    private BenchmarkPreferences ReadPreferences() => new()
    {
        Rounds = (int)rounds.Value, TimeoutSeconds = (int)timeout.Value, Concurrency = (int)concurrency.Value,
        Protocol = protocol.SelectedIndex == 1 ? "h2" : protocol.SelectedIndex == 0 ? "http/1.1" : "both",
        ScheduleEnabled = schedule.Checked, IntervalHours = (double)hours.Value, AutoSwitchEnabled = autoSwitch.Checked,
        StopOnFirstFailure = stopOnFirstFailure.Checked
    };

    private void PreferencesChanged()
    {
        if (applyingPreferences || !loaded) return;
        dirty = true; UpdateActions();
    }

    private void UpdateActions()
    {
        if (IsDisposed) return;
        var idle = !operation && !refreshing;
        testAll.Enabled = idle && loaded && !benchmarkBusy && !dirty;
        testCurrent.Enabled = idle && loaded && !benchmarkBusy && !dirty;
        cancel.Enabled = idle && benchmarkBusy;
        save.Enabled = idle && loaded && dirty;
        foreach (var control in new Control[] { rounds, timeout, concurrency, protocol, schedule, autoSwitch, stopOnFirstFailure }) control.Enabled = !operation;
        hours.Enabled = !operation && schedule.Checked;
        dirtyLabel.Text = !loaded ? "正在读取设置…" : dirty ? "有未保存设置，保存后可开始测速" : "设置已保存 · 参数用于下一次任务";
        dirtyLabel.ForeColor = dirty ? Blue : Muted;
        save.Text = dirty ? "保存设置 *" : "保存设置";
    }

    private void SetNotice(string message, bool error)
    {
        if (IsDisposed) return;
        notice.Text = Clean(message); notice.ForeColor = error ? Red : Muted;
        hints.SetToolTip(notice, Clean(message));
    }

    private static string StatusText(string value) => value switch
    {
        "preparing" => "准备中", "running" => "测速中", "completed" => "已完成", "cancelled" or "canceled" or "stopped" => "已取消",
        "cancelling" or "canceling" => "正在取消", "error" or "failed" or "interrupted" => "测试中断", "idle" => "就绪", _ => string.IsNullOrEmpty(value) ? "就绪" : value
    };
    private static string LocalTime(DateTimeOffset value) => value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
    private static string Milliseconds(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 ? value.Value.ToString("N0", CultureInfo.CurrentCulture) : "—";
    private static string SafeMessage(Exception error) => error is SafeOperationException ? Clean(error.Message) : "操作未完成，请检查本机配置后重试。";
    private static string Clean(string value)
    {
        var text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 600 ? text : text[..597] + "…";
    }

    private static NumericUpDown Number(decimal minimum, decimal maximum, decimal value, int decimals = 0) => new()
    {
        Minimum = minimum, Maximum = maximum, Value = value, Width = decimals == 0 ? 66 : 77, DecimalPlaces = decimals,
        Increment = decimals == 0 ? 1 : .1M, TextAlign = HorizontalAlignment.Right, Margin = new Padding(0, 1, 17, 0)
    };
    private static Label Detail(string text) => new() { Text = text, ForeColor = Muted, Font = new Font("Microsoft YaHei UI", 8.5F), Margin = Padding.Empty };
    private static Label Title(string text) => new() { Text = text, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), Margin = Padding.Empty, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private static void AddField(FlowLayoutPanel panel, string label, Control control)
    {
        var caption = Detail(label); caption.AutoSize = true; caption.Margin = new Padding(0, 5, 7, 0);
        panel.Controls.Add(caption); panel.Controls.Add(control);
    }
    private static Button ActionButton(string text, bool primary = false, int width = 119) => new()
    {
        Text = text, Size = new Size(width, 32), Margin = new Padding(0, 0, 9, 0), Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat,
        ForeColor = primary ? Color.White : Ink, BackColor = primary ? Blue : Color.White,
        FlatAppearance = { BorderColor = primary ? Blue : Border, BorderSize = 1, MouseOverBackColor = primary ? Color.FromArgb(29, 80, 181) : Canvas }
    };
    private static TableLayoutPanel TablePanel(string title)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 6, 10, 8), BackColor = Color.White, Margin = Padding.Empty };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 29)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(Title(title), 0, 0);
        return panel;
    }
    private static DataGridViewTextBoxColumn Column(string name, string header, int width) => new() { Name = name, HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable };
    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        AllowUserToOrderColumns = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
        BackgroundColor = Color.White, BorderStyle = BorderStyle.None, GridColor = Color.FromArgb(234, 238, 244), CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        EnableHeadersVisualStyles = false, ColumnHeadersHeight = 31, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        RowTemplate = { Height = 31 },
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Canvas, ForeColor = Muted, Padding = new Padding(5, 0, 2, 0), SelectionBackColor = Canvas },
        DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = Ink, SelectionBackColor = Color.FromArgb(230, 239, 253), SelectionForeColor = Ink, Padding = new Padding(5, 2, 2, 2) }
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) { refreshTimer.Dispose(); hints.Dispose(); }
        base.Dispose(disposing);
    }
}
