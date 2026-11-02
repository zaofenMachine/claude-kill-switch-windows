using System.Drawing;
using System.Globalization;

namespace ClaudeSafe.Manager;

/// <summary>Named source management. Closing this window does not stop the service.</summary>
public sealed class SubscriptionManagerForm : Form
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
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1500 };
    private readonly ToolTip hints = new() { AutoPopDelay = 15000 };
    private readonly DataGridView sources = Grid();
    private readonly Button add = ActionButton("添加 HTTPS 订阅", true, 150);
    private readonly Button importFile = ActionButton("导入本地文件", false, 127);
    private readonly Button update = ActionButton("更新所选订阅", false, 127);
    private readonly Button updateAll = ActionButton("更新全部订阅", false, 127);
    private readonly Button use = ActionButton("使用所选来源", true, 127);
    private readonly Button rename = ActionButton("重命名", false, 100);
    private readonly Button replaceUrl = ActionButton("更换订阅地址", false, 127);
    private readonly Button remove = ActionButton("删除所选来源", false, 127);
    private readonly Label current = Detail("正在读取节点来源…");
    private readonly Label selection = Detail("选择下方来源后，可更新或切换使用。");
    private readonly Label notice = Detail("新增订阅先保存缓存；点击“使用所选来源”后才用于专用通道。");
    private SubscriptionSnapshot? snapshot;
    private bool loaded;
    private bool operation;
    private bool refreshing;
    private bool rendering;
    private string selectedId = "";
    private string backendMessage = "";

    public event EventHandler? SourcesChanged;

    public SubscriptionManagerForm(ManagerService manager)
    {
        this.manager = manager;
        Text = "ClaudeSafe · 订阅管理与更新";
        ClientSize = new Size(1140, 710);
        MinimumSize = new Size(1010, 640);
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
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Controls.Add(page);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Navy, Padding = new Padding(23, 11, 20, 11), Margin = Padding.Empty };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label { Text = "订阅管理与更新", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 17F, FontStyle.Bold), ForeColor = Color.White, Margin = Padding.Empty }, 0, 0);
        header.Controls.Add(new Label { Text = "保存多个节点来源  ·  更新缓存  ·  手动选择当前使用的来源", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(184, 205, 229), Margin = Padding.Empty, AutoEllipsis = true }, 0, 1);
        page.Controls.Add(header, 0, 0);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(19, 15, 19, 9), Margin = Padding.Empty };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        page.Controls.Add(content, 0, 1);

        var state = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White, Padding = new Padding(11, 8, 11, 8), Margin = Padding.Empty };
        state.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        state.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        current.Dock = DockStyle.Fill; current.AutoEllipsis = true; current.ForeColor = Ink; current.Font = new Font(Font, FontStyle.Bold);
        state.Controls.Add(current, 0, 0);
        var policy = Detail("更新未使用的订阅不影响当前连接；切换来源或当前来源内容变化时，内核停止，审阅后在主窗口重新启动。");
        policy.Dock = DockStyle.Fill; policy.AutoEllipsis = true;
        state.Controls.Add(policy, 0, 1);
        content.Controls.Add(state, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 7, 0, 0) };
        actions.Controls.AddRange([add, importFile, update, updateAll, use]);
        content.Controls.Add(actions, 0, 1);

        sources.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "来源名称", MinimumWidth = 160, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 105, SortMode = DataGridViewColumnSortMode.NotSortable });
        sources.Columns.Add(Column("Active", "使用状态", 82));
        sources.Columns.Add(Column("Kind", "类型", 92));
        sources.Columns.Add(new DataGridViewTextBoxColumn { Name = "Host", HeaderText = "订阅域名", MinimumWidth = 130, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 88, SortMode = DataGridViewColumnSortMode.NotSortable });
        sources.Columns.Add(Column("Nodes", "节点数", 70));
        sources.Columns.Add(Column("Updated", "上次更新", 122));
        sources.Columns.Add(new DataGridViewTextBoxColumn { Name = "Result", HeaderText = "更新结果", MinimumWidth = 200, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 127, SortMode = DataGridViewColumnSortMode.NotSortable });
        sources.SelectionChanged += (_, _) =>
        {
            if (rendering) return;
            selectedId = SelectedItem()?.Id ?? "";
            RenderSelection(); UpdateActions();
        };
        content.Controls.Add(sources, 0, 2);

        selection.Dock = DockStyle.Fill; selection.AutoEllipsis = true; selection.TextAlign = ContentAlignment.MiddleLeft;
        content.Controls.Add(selection, 0, 3);
        var edits = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 2, 0, 0) };
        edits.Controls.AddRange([rename, replaceUrl, remove]);
        var cacheHint = Detail("更换地址后保留旧缓存，需再点击更新。");
        cacheHint.AutoSize = true; cacheHint.Margin = new Padding(8, 8, 0, 0);
        edits.Controls.Add(cacheHint);
        content.Controls.Add(edits, 0, 4);
        notice.Dock = DockStyle.Fill; notice.AutoEllipsis = true; notice.TextAlign = ContentAlignment.MiddleLeft;
        content.Controls.Add(notice, 0, 5);

        var footer = Detail("地址和节点凭据保存在本机；列表仅显示域名。下载使用 Windows 代理设置，只导入节点；新节点仍需手动批准。");
        footer.Dock = DockStyle.Fill; footer.TextAlign = ContentAlignment.MiddleCenter; footer.AutoEllipsis = true;
        footer.BackColor = Color.FromArgb(231, 237, 244);
        page.Controls.Add(footer, 0, 2);
        hints.SetToolTip(importFile, "导入为新的本地来源并立即使用；专用内核将停止，需在主窗口审阅节点后重新启动。");
        hints.SetToolTip(update, "重新下载已保存的 HTTPS 订阅。下载失败保留缓存；当前来源内容变化时停止专用内核。");
        hints.SetToolTip(updateAll, "更新所有 HTTPS 订阅，最多同时下载 3 份；一个失败仍继续其他订阅。本地文件不从网络更新。");
        hints.SetToolTip(use, "选择此来源后停止专用内核。已有批准仅在节点身份完全匹配时继续有效；重新启动后默认 REJECT。");
    }

    private void WireActions()
    {
        add.Click += async (_, _) =>
        {
            using var dialog = new EntryDialog("添加 HTTPS 订阅", true, true, "", "下载成功后保存为新来源，暂不切换；可稍后点击“使用所选来源”。", "下载并保存");
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await RunOperationAsync("正在下载并保存新订阅…", () => manager.AddSubscriptionAsync(dialog.SourceName, dialog.SubscriptionUrl));
        };
        importFile.Click += async (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "导入并使用本地节点来源", Filter = "Mihomo 配置 (*.yaml;*.yml;*.json)|*.yaml;*.yml;*.json|所有文件 (*.*)|*.*", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await RunOperationAsync("正在导入本地来源，内核将保持停止…", () => manager.ImportFileAsync(dialog.FileName), true);
        };
        update.Click += async (_, _) =>
        {
            var item = SelectedItem();
            if (item?.Kind != "https") return;
            await RunOperationAsync("正在更新所选订阅；下载失败会保留缓存…", () => manager.UpdateSubscriptionAsync(item.Id), true);
        };
        updateAll.Click += async (_, _) => await RunOperationAsync("正在更新全部 HTTPS 订阅；一个失败仍继续其他订阅…", manager.UpdateAllSubscriptionsAsync, true);
        use.Click += async (_, _) =>
        {
            var item = SelectedItem();
            if (item == null || item.Active) return;
            await RunOperationAsync("正在切换节点来源，内核将保持停止…", () => manager.UseSubscriptionAsync(item.Id), true);
        };
        rename.Click += async (_, _) =>
        {
            var item = SelectedItem();
            if (item == null) return;
            using var dialog = new EntryDialog("重命名节点来源", true, false, item.Name, "仅修改显示名称，保留节点缓存和当前连接。", "保存名称");
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await RunOperationAsync("正在保存来源名称…", () => manager.RenameSubscriptionAsync(item.Id, dialog.SourceName));
        };
        replaceUrl.Click += async (_, _) =>
        {
            var item = SelectedItem();
            if (item?.Kind != "https") return;
            using var dialog = new EntryDialog("更换订阅地址", false, true, "", "请输入完整的新地址。保留现有缓存和当前连接，保存后需再点击“更新所选订阅”。", "保存地址");
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await RunOperationAsync("正在保存新的订阅地址…", () => manager.SetSubscriptionUrlAsync(item.Id, dialog.SubscriptionUrl));
        };
        remove.Click += async (_, _) =>
        {
            var item = SelectedItem();
            if (item == null) return;
            var consequence = item.Active
                ? "这是当前使用的来源。删除后将停止专用内核，并清空当前节点来源。"
                : "此来源的本地缓存和保存的订阅地址将删除，当前连接保持不变。";
            var result = MessageBox.Show(this, $"删除来源“{Clean(item.Name)}”？\n\n{consequence}", "删除节点来源", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);
            if (result != DialogResult.OK) return;
            await RunOperationAsync("正在删除节点来源…", () => manager.RemoveSubscriptionAsync(item.Id), item.Active);
        };
    }

    private async Task RunOperationAsync(string progress, Func<Task> work, bool sourceMayChange = false)
    {
        if (operation || refreshing || snapshot?.Busy == true || IsDisposed) return;
        operation = true; UpdateActions(); SetNotice(progress, false);
        Exception? failure = null;
        try { await work(); }
        catch (Exception error) { failure = error; }
        finally
        {
            operation = false;
            if (!IsDisposed)
            {
                await RefreshAsync();
                if (failure is not null) SetNotice(SafeMessage(failure), true);
                else SetNotice(snapshot?.Message ?? "操作已完成。", false);
                if (sourceMayChange) SourcesChanged?.Invoke(this, EventArgs.Empty);
                UpdateActions();
            }
        }
    }

    private async Task RefreshAsync()
    {
        if (operation || refreshing || IsDisposed) return;
        refreshing = true; UpdateActions();
        try
        {
            var state = await manager.SubscriptionsAsync();
            if (IsDisposed) return;
            snapshot = state; loaded = true;
            RenderSources();
            if (!string.IsNullOrWhiteSpace(state.Message) && backendMessage != state.Message)
            {
                backendMessage = state.Message;
                SetNotice(state.Message, false);
            }
        }
        catch (Exception error) { SetNotice(SafeMessage(error), true); }
        finally { refreshing = false; if (!IsDisposed) UpdateActions(); }
    }

    private void RenderSources()
    {
        if (snapshot == null) return;
        var active = snapshot.Items.FirstOrDefault(item => item.Active);
        current.Text = active == null ? $"当前没有使用的来源  ·  已保存 {snapshot.Items.Count} 份" : $"当前来源：{Clean(active.Name)}  ·  {active.NodeCount} 个节点  ·  已保存 {snapshot.Items.Count} 份";
        hints.SetToolTip(current, current.Text);
        var keep = SelectedItem()?.Id ?? selectedId;
        var scroll = sources.FirstDisplayedScrollingRowIndex;
        rendering = true; sources.SuspendLayout();
        try
        {
            sources.Rows.Clear();
            foreach (var item in snapshot.Items)
            {
                var error = !string.IsNullOrWhiteSpace(item.LastError);
                var result = error ? "更新失败 · 保留原缓存" : item.LastUpdatedUtc.HasValue ? "已缓存" : "尚无更新记录";
                var index = sources.Rows.Add(Clean(item.Name), item.Active ? "使用中" : "未使用", item.Kind == "https" ? "HTTPS 订阅" : "本地文件",
                    string.IsNullOrEmpty(item.MaskedUrl) ? "—" : Clean(item.MaskedUrl), item.NodeCount,
                    item.LastUpdatedUtc.HasValue ? item.LastUpdatedUtc.Value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture) : "—", result);
                var row = sources.Rows[index]; row.Tag = item;
                row.Cells["Active"].Style.ForeColor = item.Active ? Green : Muted;
                row.Cells["Result"].Style.ForeColor = error ? Red : Muted;
                row.Cells["Name"].ToolTipText = Clean(item.Name);
                row.Cells["Host"].ToolTipText = string.IsNullOrEmpty(item.MaskedUrl) ? "本地配置，不保存文件的原始路径。" : "仅显示订阅域名：" + Clean(item.MaskedUrl);
                row.Cells["Result"].ToolTipText = error ? Clean(item.LastError) : "使用保存的节点缓存；新节点仍需手动批准。";
            }
            if (!snapshot.Items.Any(item => item.Id == keep)) keep = active?.Id ?? snapshot.Items.FirstOrDefault()?.Id ?? "";
            sources.ClearSelection();
            foreach (DataGridViewRow row in sources.Rows) row.Selected = (row.Tag as SubscriptionSummary)?.Id == keep;
            selectedId = keep;
            if (scroll >= 0 && scroll < sources.Rows.Count) sources.FirstDisplayedScrollingRowIndex = scroll;
        }
        finally { sources.ResumeLayout(); rendering = false; }
        RenderSelection();
    }

    private void RenderSelection()
    {
        var item = SelectedItem();
        selection.Text = item == null ? "选择一个已保存来源，可更新订阅或切换使用。"
            : !string.IsNullOrWhiteSpace(item.LastError) ? $"{Clean(item.Name)}：{Clean(item.LastError)}\n现有缓存保留；可检查地址后重试更新。"
            : item.Active ? $"{Clean(item.Name)} 正在使用。更新内容不变时连接保留；节点内容变化时停止内核，等待重新启动。"
            : $"{Clean(item.Name)} 尚未使用。{(item.Kind == "https" ? "可更新缓存，" : "")}点击“使用所选来源”后切换，并在主窗口审阅节点、重新启动。";
        selection.ForeColor = !string.IsNullOrWhiteSpace(item?.LastError) ? Red : Muted;
        hints.SetToolTip(selection, selection.Text);
    }

    private SubscriptionSummary? SelectedItem() => sources.SelectedRows.Count == 0 ? null : sources.SelectedRows[0].Tag as SubscriptionSummary;

    private void UpdateActions()
    {
        if (IsDisposed) return;
        var idle = loaded && !operation && !refreshing && snapshot?.Busy != true;
        var item = SelectedItem();
        add.Enabled = idle; importFile.Enabled = idle;
        update.Enabled = idle && item?.Kind == "https";
        updateAll.Enabled = idle && snapshot!.Items.Any(source => source.Kind == "https");
        use.Enabled = idle && item != null && !item.Active;
        rename.Enabled = idle && item != null;
        replaceUrl.Enabled = idle && item?.Kind == "https";
        remove.Enabled = idle && item != null;
        sources.Enabled = !operation;
    }

    private void SetNotice(string text, bool error)
    {
        if (IsDisposed) return;
        notice.Text = Clean(text); notice.ForeColor = error ? Red : Muted;
        hints.SetToolTip(notice, notice.Text);
    }

    private static string SafeMessage(Exception error) => error is SafeOperationException ? Clean(error.Message) : "操作未完成，请检查本机配置后重试。";
    private static string Clean(string value)
    {
        var text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 600 ? text : text[..597] + "…";
    }
    private static Label Detail(string text) => new() { Text = text, ForeColor = Muted, Font = new Font("Microsoft YaHei UI", 8.5F), Margin = Padding.Empty };
    private static Button ActionButton(string text, bool primary = false, int width = 119) => new()
    {
        Text = text, Size = new Size(width, 32), Margin = new Padding(0, 0, 9, 0), Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat,
        ForeColor = primary ? Color.White : Ink, BackColor = primary ? Blue : Color.White,
        FlatAppearance = { BorderColor = primary ? Blue : Border, BorderSize = 1, MouseOverBackColor = primary ? Color.FromArgb(29, 80, 181) : Canvas }
    };
    private static DataGridViewTextBoxColumn Column(string name, string header, int width) => new() { Name = name, HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable };
    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        AllowUserToOrderColumns = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
        BackgroundColor = Color.White, BorderStyle = BorderStyle.None, GridColor = Color.FromArgb(234, 238, 244), CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        EnableHeadersVisualStyles = false, ColumnHeadersHeight = 33, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        RowTemplate = { Height = 35 },
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Canvas, ForeColor = Muted, Padding = new Padding(5, 0, 2, 0), SelectionBackColor = Canvas },
        DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = Ink, SelectionBackColor = Color.FromArgb(230, 239, 253), SelectionForeColor = Ink, Padding = new Padding(5, 2, 2, 2) }
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) { refreshTimer.Dispose(); hints.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed class EntryDialog : Form
    {
        private readonly TextBox name = new() { Dock = DockStyle.Fill, MaxLength = 80 };
        private readonly TextBox address = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, MaxLength = 8192 };
        public string SourceName => name.Text.Trim();
        public string SubscriptionUrl => address.Text.Trim();

        public EntryDialog(string title, bool withName, bool withAddress, string initialName, string explanation, string submit)
        {
            Text = title;
            ClientSize = new Size(610, withName && withAddress ? 290 : withAddress ? 235 : 200);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei UI", 9F); BackColor = Color.White; ForeColor = Ink;
            AutoScaleMode = AutoScaleMode.Dpi;
            var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(20, 15, 20, 14), Margin = Padding.Empty };
            foreach (var height in new float[] { withName ? 24 : 0, withName ? 36 : 0, withAddress ? 24 : 0, withAddress ? 35 : 0, withAddress ? 30 : 0 })
                page.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            Controls.Add(page);
            name.Text = initialName;
            var nameLabel = Detail("来源名称"); nameLabel.Dock = DockStyle.Fill; nameLabel.Visible = withName;
            name.Visible = withName;
            page.Controls.Add(nameLabel, 0, 0); page.Controls.Add(name, 0, 1);
            var urlLabel = Detail("HTTPS 订阅地址"); urlLabel.Dock = DockStyle.Fill; urlLabel.Visible = withAddress;
            address.Visible = withAddress;
            page.Controls.Add(urlLabel, 0, 2); page.Controls.Add(address, 0, 3);
            var show = new CheckBox { Text = "显示正在输入的地址", AutoSize = true, Visible = withAddress, Margin = Padding.Empty };
            show.CheckedChanged += (_, _) => address.UseSystemPasswordChar = !show.Checked;
            page.Controls.Add(show, 0, 4);
            var help = Detail(explanation); help.Dock = DockStyle.Fill; help.TextAlign = ContentAlignment.MiddleLeft;
            page.Controls.Add(help, 0, 5);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            var cancel = ActionButton("取消", false, 104); cancel.DialogResult = DialogResult.Cancel; cancel.Margin = Padding.Empty;
            var ok = ActionButton(submit, true, 125);
            ok.Click += (_, _) =>
            {
                if (withName && (SourceName.Length == 0 || SourceName.Any(char.IsControl)))
                {
                    MessageBox.Show(this, "请输入 1–80 字的名称，不要包含换行或控制字符。", "检查名称", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    name.Focus(); return;
                }
                if (withAddress && (!Uri.TryCreate(SubscriptionUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || SubscriptionUrl.Any(char.IsWhiteSpace) || SubscriptionUrl.Any(char.IsControl)))
                {
                    MessageBox.Show(this, "请输入完整的 HTTPS 订阅地址，不含空格、用户名或片段。", "检查地址", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    address.Focus(); return;
                }
                DialogResult = DialogResult.OK;
            };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            page.Controls.Add(buttons, 0, 6);
            AcceptButton = ok; CancelButton = cancel;
            Shown += (_, _) => { if (withName) { name.Focus(); name.SelectAll(); } else address.Focus(); };
        }
    }
}
