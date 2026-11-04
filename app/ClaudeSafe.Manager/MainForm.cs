using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClaudeSafe.Manager;

public sealed class MainForm : Form
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
    private readonly string? screenshotPath;
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 3000 };
    private readonly NotifyIcon tray = new();
    private readonly NodeListView nodeList = new();
    private DataGridView nodes => nodeList.Grid;
    private readonly ComboBox interfaces = new();
    private readonly Label coreValue = Value("读取中…");
    private readonly Label coreDetail = Detail("尚未启动专用内核");
    private readonly Label selectedValue = Value("REJECT");
    private readonly Label selectedDetail = Detail("默认阻断，手动选择批准节点");
    private readonly Label exitValue = Value("尚未测试");
    private readonly Label exitDetail = Detail("匿名检测，不发送聊天请求");
    private readonly Label listenerValue = Value("127.0.0.1:17987");
    private readonly Label listenerDetail = Detail("Desktop 固定代理入口");
    private readonly Label status = Detail("就绪");
    private readonly Button start = ActionButton("启动内核", true);
    private readonly Button stop = ActionButton("停止内核");
    private readonly Button reject = ActionButton("立即阻断");
    private readonly Button probe = ActionButton("检测专用出口");
    private readonly Button desktopBenchmark = ActionButton("Desktop 测速 / 定时");
    private readonly Button select = ActionButton("使用所选节点", true);
    private readonly Button approve = ActionButton("批准所选节点");
    private readonly Button revoke = ActionButton("移除批准");
    private readonly Button importFile = ActionButton("导入 YAML / JSON");
    private readonly Button subscriptions = ActionButton("订阅管理 / 更新");
    private readonly Button saveInterface = ActionButton("保存出站网卡");
    private bool busy;
    private bool refreshing;
    private bool exiting;
    private bool firstTrayTip = true;
    private bool running;
    private string selectedNode = "REJECT";
    private string savedInterface = "";
    private DesktopBenchmarkForm? benchmarkWindow;
    private SubscriptionManagerForm? subscriptionWindow;

    public MainForm(ManagerService manager, string? screenshotPath = null)
    {
        this.manager = manager;
        this.screenshotPath = screenshotPath;
        Text = "ClaudeSafe · 专用通道管理器";
        ClientSize = new Size(1120, 760);
        MinimumSize = new Size(1000, 740);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        ForeColor = Ink;
        BackColor = Canvas;
        Icon = SystemIcons.Shield;
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildLayout();
        BuildTray();
        WireActions();
        UpdateActions();
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) =>
        {
            await RefreshAsync();
            refreshTimer.Start();
            if (this.screenshotPath != null)
            {
                await Task.Delay(800);
                try
                {
                    using var image = new Bitmap(Width, Height);
                    DrawToBitmap(image, new Rectangle(0, 0, Width, Height));
                    var file = Path.GetFullPath(this.screenshotPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    image.Save(file, ImageFormat.Png);
                }
                catch { SetStatus("预览图保存失败，请检查输出目录。", true); }
            }
        };
        FormClosing += (_, e) =>
        {
            if (exiting || e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing)
            {
                refreshTimer.Stop();
                tray.Visible = false;
                return;
            }
            e.Cancel = true;
            Hide();
            if (firstTrayTip)
            {
                firstTrayTip = false;
                tray.ShowBalloonTip(3500, "ClaudeSafe 仍在运行", "双击托盘图标打开。要停止内核并退出，请使用托盘菜单。", ToolTipIcon.Info);
            }
        };
    }

    private void BuildLayout()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty };
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        Controls.Add(page);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Navy, Margin = Padding.Empty, Padding = new Padding(25, 12, 25, 12) };
        var title = new Label { Text = "ClaudeSafe", Font = new Font(Font.FontFamily, 22F, FontStyle.Bold), ForeColor = Color.White, AutoSize = true, Location = new Point(25, 10) };
        var subtitle = new Label { Text = "专用通道管理器  /  独立 Mihomo 内核", Font = new Font(Font.FontFamily, 10F), ForeColor = Color.FromArgb(172, 194, 220), AutoSize = true, Location = new Point(28, 54) };
        var tag = new Label { Text = "仅管理 Claude 专用实例", ForeColor = Color.FromArgb(199, 216, 237), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        header.Controls.AddRange(new Control[] { title, subtitle, tag });
        header.Resize += (_, _) => tag.Location = new Point(header.ClientSize.Width - tag.PreferredWidth - 27, 33);
        page.Controls.Add(header, 0, 0);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(22, 18, 22, 8), Margin = Padding.Empty };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        page.Controls.Add(content, 0, 1);

        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        for (var i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cards.Controls.Add(Card("内核状态", coreValue, coreDetail), 0, 0);
        cards.Controls.Add(Card("当前通道", selectedValue, selectedDetail), 1, 0);
        cards.Controls.Add(Card("出口检查", exitValue, exitDetail), 2, 0);
        cards.Controls.Add(Card("专用入口", listenerValue, listenerDetail), 3, 0);
        listenerValue.Font = new Font("Consolas", 13F, FontStyle.Bold);
        content.Controls.Add(cards, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, Padding = new Padding(0, 6, 0, 0), WrapContents = false };
        actions.Controls.AddRange(new Control[] { start, stop, reject, probe, desktopBenchmark });
        var hint = Detail("停止或阻断后，专用通道不可用");
        hint.AutoSize = true;
        hint.Margin = new Padding(9, 12, 0, 0);
        actions.Controls.Add(hint);
        content.Controls.Add(actions, 0, 1);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        content.Controls.Add(body, 0, 2);

        var listPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 12, 0) };
        listPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        listPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
        nodes.SelectionChanged += (_, _) => UpdateActions();
        nodes.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && select.Enabled) await SelectNodeAsync(); };
        listPanel.Controls.Add(nodeList, 0, 0);
        var nodeActions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 9, 0, 0), WrapContents = false };
        nodeActions.Controls.AddRange(new Control[] { select, approve, revoke });
        listPanel.Controls.Add(nodeActions, 0, 1);
        body.Controls.Add(listPanel, 0, 0);

        var side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 12, Padding = new Padding(15, 12, 15, 10), BackColor = Color.White, Margin = Padding.Empty };
        var rowHeights = new float[] { 26, 34, 33, 37, 8, 26, 37, 37, 43, 8, 26 };
        foreach (var h in rowHeights) side.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        side.Controls.Add(SectionTitle("出站网卡"), 0, 0);
        var interfaceHint = Detail("选择物理网络接口，避免走入其他 TUN。");
        interfaceHint.Dock = DockStyle.Fill;
        side.Controls.Add(interfaceHint, 0, 1);
        interfaces.DropDownStyle = ComboBoxStyle.DropDownList;
        interfaces.Dock = DockStyle.Top;
        interfaces.Margin = new Padding(0, 0, 0, 5);
        foreach (var item in ManagerService.PhysicalInterfaces()) interfaces.Items.Add(item);
        side.Controls.Add(interfaces, 0, 2);
        saveInterface.Dock = DockStyle.Top;
        saveInterface.Margin = new Padding(0, 0, 0, 5);
        side.Controls.Add(saveInterface, 0, 3);
        side.Controls.Add(SectionTitle("节点来源"), 0, 5);
        importFile.Dock = DockStyle.Top;
        subscriptions.Dock = DockStyle.Top;
        importFile.Margin = new Padding(0, 0, 0, 5);
        subscriptions.Margin = new Padding(0, 0, 0, 5);
        side.Controls.Add(importFile, 0, 6);
        side.Controls.Add(subscriptions, 0, 7);
        var importHint = Detail("保存多份订阅并更新节点；新节点需手动批准。");
        importHint.Dock = DockStyle.Fill;
        side.Controls.Add(importHint, 0, 8);
        side.Controls.Add(SectionTitle("连接规则"), 0, 10);
        var behavior = Detail("仅批准节点 · 默认 REJECT\n定时切换默认关闭\n\n关闭窗口保留托盘运行");
        behavior.Dock = DockStyle.Fill;
        side.Controls.Add(behavior, 0, 11);
        body.Controls.Add(side, 1, 0);

        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.AutoEllipsis = true;
        content.Controls.Add(status, 0, 3);
        var footer = new Label { Text = "本工具不设置系统代理或 TUN  ·  Clash Verge 可继续管理日常代理  ·  WFP / AppLocker 需独立维护", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Muted, BackColor = Color.FromArgb(231, 237, 244), Font = new Font(Font.FontFamily, 8.5F), Margin = Padding.Empty };
        page.Controls.Add(footer, 0, 2);
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 ClaudeSafe", null, (_, _) => RestoreWindow());
        menu.Items.Add("Desktop 测速 / 定时", null, (_, _) => OpenBenchmarkWindow());
        menu.Items.Add("订阅管理 / 更新", null, (_, _) => OpenSubscriptionWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("停止内核并退出", null, async (_, _) =>
        {
            if (busy || refreshing) { RestoreWindow(); SetStatus("当前操作完成后可退出。", false); return; }
            exiting = true;
            busy = true;
            refreshTimer.Stop();
            UpdateActions();
            try
            {
                ClearProbe();
                await manager.StopAsync();
                manager.Dispose();
                Close();
            }
            catch (Exception ex)
            {
                exiting = false;
                busy = false;
                RestoreWindow();
                SetStatus(ex.Message, true);
                refreshTimer.Start();
                UpdateActions();
            }
        });
        tray.Icon = Icon;
        tray.Text = "ClaudeSafe · 专用通道";
        tray.Visible = true;
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => RestoreWindow();
    }

    private void WireActions()
    {
        desktopBenchmark.Click += (_, _) => OpenBenchmarkWindow();
        subscriptions.Click += (_, _) => OpenSubscriptionWindow();
        start.Click += async (_, _) => await RunAsync("正在启动专用内核…", async () => { ClearProbe(); await manager.StartAsync(); SetStatus("专用内核已启动。", false); });
        stop.Click += async (_, _) => await RunAsync("正在停止专用内核…", async () => { ClearProbe(); await manager.StopAsync(); SetStatus("专用内核已停止。", false); });
        reject.Click += async (_, _) => await RunAsync("正在阻断专用通道…", async () => { await manager.SelectAsync("REJECT"); ClearProbe(); SetStatus("已切换 REJECT，专用通道拒绝连接。", false); });
        probe.Click += async (_, _) => await RunAsync("正在检测专用出口，请稍候…", async () =>
        {
            var result = await manager.ProbeAsync();
            exitValue.Text = result.Success ? result.Country : "检查失败";
            exitValue.ForeColor = result.Success ? Green : Red;
            exitDetail.Text = result.Success ? $"{result.ElapsedMs:N0} ms · 当前通道采样" : "请检查节点或出站网卡";
            SetStatus(result.Message, !result.Success);
        });
        select.Click += async (_, _) => await SelectNodeAsync();
        approve.Click += async (_, _) =>
        {
            var item = SelectedItem();
            if (item == null) return;
            var answer = MessageBox.Show(this, "将以下节点加入 Claude 专用通道批准名单：\n\n" + item.Name + "\n\n请确认这是你打算使用的节点。批准只匹配当前节点身份，不保证出口地区或账号安全。", "批准节点", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;
            await RunAsync("正在保存批准名单…", async () => { ClearProbe(); await manager.ApproveAsync(item.Name); SetStatus("已批准所选节点，可手动切换使用。", false); });
        };
        revoke.Click += async (_, _) =>
        {
            var item = SelectedItem();
            if (item == null) return;
            await RunAsync("正在移除批准…", async () => { ClearProbe(); await manager.RevokeAsync(item.Name); SetStatus("已移除节点批准。", false); });
        };
        importFile.Click += async (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "导入 Mihomo 节点配置", Filter = "Mihomo 配置 (*.yaml;*.yml;*.json)|*.yaml;*.yml;*.json|所有文件 (*.*)|*.*", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await RunAsync("正在导入并使用本地节点配置…", async () => { await manager.ImportFileAsync(dialog.FileName); ClearProbe(); SetStatus("本地配置已保存并使用，内核保持停止。请审阅批准状态后重新启动。", false); });
        };
        saveInterface.Click += async (_, _) =>
        {
            var name = interfaces.SelectedItem as string;
            if (string.IsNullOrEmpty(name)) { SetStatus("请先选择实际使用的物理网卡。", true); return; }
            await RunAsync("正在保存出站网卡…", async () => { await manager.SetInterfaceAsync(name); ClearProbe(); SetStatus("已保存出站网卡。请重新检测专用出口。", false); });
        };
    }

    private async Task SelectNodeAsync()
    {
        var item = SelectedItem();
        if (item == null || !item.Approved || !item.Eligible) return;
            await RunAsync("正在切换专用节点…", async () => { await manager.SelectAsync(item.Name); ClearProbe(); SetStatus("已使用所选批准节点。请检测专用出口。", false); });
    }

    private async Task RunAsync(string message, Func<Task> operation)
    {
        if (busy || refreshing) return;
        busy = true;
        UpdateActions();
        SetStatus(message, false);
        try { await operation(); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally
        {
            busy = false;
            await RefreshAsync();
            UpdateActions();
        }
    }

    private async Task RefreshAsync()
    {
        if (busy || refreshing || IsDisposed) return;
        refreshing = true;
        try
        {
            var state = await manager.SnapshotAsync();
            if (IsDisposed) return;
            if (running != state.Running || savedInterface != state.InterfaceName) ClearProbe();
            running = state.Running;
            coreValue.Text = running ? "运行中" : "已停止";
            coreValue.ForeColor = running ? Green : Muted;
            coreDetail.Text = running ? "专用实例 · PID " + state.ProcessId : "启动后仅接收专用入口连接";
            selectedValue.Text = string.IsNullOrEmpty(state.Selected) ? "REJECT" : state.Selected;
            var selectedFontSize = selectedValue.Text.Length > 16 ? 11F : 15F;
            if (Math.Abs(selectedValue.Font.Size - selectedFontSize) > 0.1F)
            {
                var oldFont = selectedValue.Font;
                selectedValue.Font = new Font(Font.FontFamily, selectedFontSize, FontStyle.Bold);
                oldFont.Dispose();
            }
            selectedValue.ForeColor = state.Selected == "REJECT" ? Red : Ink;
            selectedDetail.Text = state.Selected == "REJECT" ? "专用通道当前拒绝连接"
                : state.AutoSwitchEnabled ? "批准节点 · 定时失败后可自动切换" : "批准节点 · 自动切换未启用";
            if (selectedNode != state.Selected) ClearProbe();
            selectedNode = state.Selected;
            if (savedInterface != state.InterfaceName || interfaces.SelectedItem == null)
            {
                savedInterface = state.InterfaceName;
                if (!string.IsNullOrEmpty(savedInterface))
                {
                    if (!interfaces.Items.Contains(savedInterface)) interfaces.Items.Add(savedInterface);
                    interfaces.SelectedItem = savedInterface;
                }
            }
            nodeList.SetNodes(state.Nodes, state.Selected);
            tray.Text = "ClaudeSafe · " + (running ? "运行中" : "已停止");
            if (!string.IsNullOrWhiteSpace(state.Message) && status.Text == "就绪") SetStatus(state.Message, false);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { refreshing = false; UpdateActions(); }
    }

    private NodeSummary? SelectedItem() => nodes.SelectedRows.Count == 0 ? null : nodes.SelectedRows[0].Tag as NodeSummary;

    private void UpdateActions()
    {
        var idle = !busy && !refreshing;
        var item = SelectedItem();
        start.Enabled = idle && !running;
        stop.Enabled = idle && running;
        reject.Enabled = idle && selectedNode != "REJECT";
        probe.Enabled = idle && running && selectedNode != "REJECT";
        select.Enabled = idle && running && item?.Approved == true && item.Eligible && item.Name != selectedNode;
        approve.Enabled = idle && item != null && !item.Approved && item.CanApprove;
        revoke.Enabled = idle && item?.Approved == true;
        importFile.Enabled = idle;
        subscriptions.Enabled = idle;
        saveInterface.Enabled = idle;
        interfaces.Enabled = idle;
    }

    private void ClearProbe()
    {
        exitValue.Text = "尚未测试";
        exitValue.ForeColor = Ink;
        exitDetail.Text = "匿名检测，不发送聊天请求";
    }

    private void SetStatus(string message, bool error)
    {
        status.Text = (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
        if (status.Text.Length > 450) status.Text = status.Text[..447] + "…";
        status.ForeColor = error ? Red : Muted;
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OpenBenchmarkWindow()
    {
        if (exiting) return;
        if (benchmarkWindow == null || benchmarkWindow.IsDisposed)
        {
            benchmarkWindow = new DesktopBenchmarkForm(manager);
            benchmarkWindow.FormClosed += (_, _) => benchmarkWindow = null;
            benchmarkWindow.Show(this);
        }
        else
        {
            benchmarkWindow.Show();
            benchmarkWindow.WindowState = FormWindowState.Normal;
            benchmarkWindow.Activate();
        }
    }

    private void OpenSubscriptionWindow()
    {
        if (exiting) return;
        if (subscriptionWindow == null || subscriptionWindow.IsDisposed)
        {
            subscriptionWindow = new SubscriptionManagerForm(manager);
            subscriptionWindow.SourcesChanged += async (_, _) =>
            {
                if (IsDisposed || exiting) return;
                ClearProbe();
                await RefreshAsync();
            };
            subscriptionWindow.FormClosed += (_, _) => subscriptionWindow = null;
            subscriptionWindow.Show(this);
        }
        else
        {
            subscriptionWindow.Show();
            subscriptionWindow.WindowState = FormWindowState.Normal;
            subscriptionWindow.Activate();
        }
    }

    private static Label Value(string text) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold), ForeColor = Ink, Margin = Padding.Empty };
    private static Label Detail(string text) => new() { Text = text, Font = new Font("Microsoft YaHei UI", 8.5F), ForeColor = Muted, Margin = Padding.Empty };
    private static Label SectionTitle(string text) => new() { Text = text, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), ForeColor = Ink, Margin = Padding.Empty };

    private static Control Card(string title, Label value, Label detail)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.White, Margin = new Padding(0, 0, 10, 0), Padding = new Padding(14, 10, 10, 9) };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        card.Controls.Add(new Label { Text = title, ForeColor = Muted, Dock = DockStyle.Fill, Margin = Padding.Empty, Font = new Font("Microsoft YaHei UI", 8.5F) }, 0, 0);
        card.Controls.Add(value, 0, 1);
        detail.Dock = DockStyle.Fill;
        detail.AutoEllipsis = true;
        detail.TextAlign = ContentAlignment.MiddleLeft;
        card.Controls.Add(detail, 0, 2);
        return card;
    }

    private static Button ActionButton(string text, bool primary = false) => new()
    {
        Text = text, AutoSize = false, Size = new Size(text.Length > 9 ? 165 : 119, 34), Margin = new Padding(0, 0, 8, 0),
        BackColor = primary ? Blue : Color.White, ForeColor = primary ? Color.White : Ink, FlatStyle = FlatStyle.Flat,
        Font = new Font("Microsoft YaHei UI", 9F), Cursor = Cursors.Hand,
        FlatAppearance = { BorderColor = primary ? Blue : Border, BorderSize = 1, MouseOverBackColor = primary ? Color.FromArgb(29, 80, 181) : Canvas }
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            benchmarkWindow?.Dispose();
            subscriptionWindow?.Dispose();
            refreshTimer.Dispose();
            tray.Visible = false;
            tray.Dispose();
        }
        base.Dispose(disposing);
    }

}
