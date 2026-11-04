using System.ComponentModel;
using System.Drawing;

namespace ClaudeSafe.Manager;

/// <summary>Display state only; sorting and filtering never change the node source or approvals.</summary>
public sealed class NodeListView : UserControl
{
    private static readonly Color Ink = Color.FromArgb(28, 44, 63);
    private static readonly Color Muted = Color.FromArgb(99, 116, 135);
    private static readonly Color Blue = Color.FromArgb(35, 97, 213);
    private static readonly Color Green = Color.FromArgb(25, 127, 92);
    private static readonly Color Red = Color.FromArgb(173, 55, 65);
    private static readonly Color Canvas = Color.FromArgb(242, 245, 249);
    private static readonly Color Border = Color.FromArgb(220, 227, 235);
    private readonly Label count = new() { Dock = DockStyle.Fill, ForeColor = Muted, AutoEllipsis = true, Margin = Padding.Empty };
    private readonly TextBox search = new()
    {
        Dock = DockStyle.Fill, PlaceholderText = "输入节点名筛选", MaxLength = 200,
        AccessibleName = "节点名称筛选", Margin = new Padding(0, 4, 9, 0)
    };
    private readonly CheckBox approvedOnly = new()
    {
        Text = "只显示已批准", Appearance = Appearance.Button, AutoSize = false,
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, FlatStyle = FlatStyle.Flat,
        Margin = new Padding(0, 0, 8, 3), Cursor = Cursors.Hand, AccessibleName = "只显示已批准节点",
        FlatAppearance = { BorderColor = Border, CheckedBackColor = Color.FromArgb(230, 239, 253) }
    };
    private readonly Button clear = new()
    {
        Text = "清除筛选", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
        Margin = new Padding(0, 0, 0, 3), Cursor = Cursors.Hand, AccessibleName = "清除节点筛选",
        FlatAppearance = { BorderColor = Border }
    };
    private List<NodeSummary> allNodes = [];
    private RowState[] rendered = [];
    private string activeNode = "REJECT";
    private bool changingFilters;

    public DataGridView Grid { get; } = new();
    [DefaultValue("")]
    public string NameFilter { get => search.Text; set => search.Text = value ?? ""; }
    [DefaultValue(false)]
    public bool ApprovedOnly { get => approvedOnly.Checked; set => approvedOnly.Checked = value; }
    public string CountText => count.Text;

    public NodeListView()
    {
        Dock = DockStyle.Fill; Margin = Padding.Empty; BackColor = Color.White; ForeColor = Ink;
        Font = new Font("Microsoft YaHei UI", 9F);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = "节点清单", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 11F, FontStyle.Bold), Margin = Padding.Empty
        }, 0, 0);
        layout.Controls.Add(count, 0, 1);
        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 127));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        filters.Controls.Add(search, 0, 0); filters.Controls.Add(approvedOnly, 1, 0); filters.Controls.Add(clear, 2, 0);
        layout.Controls.Add(filters, 0, 2);
        ConfigureGrid(); layout.Controls.Add(Grid, 0, 3); Controls.Add(layout);
        search.TextChanged += (_, _) => FiltersChanged();
        approvedOnly.CheckedChanged += (_, _) => FiltersChanged();
        clear.Click += (_, _) => ClearFilters();
        Render(false);
    }

    private void ConfigureGrid()
    {
        Grid.Dock = DockStyle.Fill; Grid.Margin = Padding.Empty; Grid.ReadOnly = true;
        Grid.AllowUserToAddRows = false; Grid.AllowUserToDeleteRows = false;
        Grid.AllowUserToResizeRows = false; Grid.AllowUserToOrderColumns = false;
        Grid.MultiSelect = false; Grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        Grid.RowHeadersVisible = false; Grid.BackgroundColor = Color.White; Grid.BorderStyle = BorderStyle.None;
        Grid.GridColor = Color.FromArgb(234, 238, 244); Grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        Grid.EnableHeadersVisualStyles = false;
        Grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        { BackColor = Canvas, ForeColor = Muted, Font = Font, Padding = new Padding(7, 0, 3, 0), SelectionBackColor = Canvas };
        Grid.ColumnHeadersHeight = 33; Grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        Grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White, ForeColor = Ink, SelectionBackColor = Color.FromArgb(230, 239, 253),
            SelectionForeColor = Ink, Padding = new Padding(7, 2, 3, 2), Font = Font
        };
        Grid.RowTemplate.Height = 38;
        Grid.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "NodeName", HeaderText = "节点名称", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 200 });
        Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Protocol", HeaderText = "协议", Width = 78 });
        Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Approval", HeaderText = "批准", Width = 70 });
        Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Availability", HeaderText = "状态", Width = 100 });
        foreach (DataGridViewColumn column in Grid.Columns) column.SortMode = DataGridViewColumnSortMode.Automatic;
    }

    public void SetNodes(IReadOnlyList<NodeSummary> nodes, string selectedNode)
    {
        allNodes = nodes.ToList(); activeNode = selectedNode;
        Render(false);
    }

    public void ClearFilters()
    {
        changingFilters = true;
        try { search.Clear(); approvedOnly.Checked = false; }
        finally { changingFilters = false; }
        FiltersChanged();
    }

    private void FiltersChanged()
    {
        if (!changingFilters) Render(true);
    }

    private void Render(bool resetScroll)
    {
        var term = search.Text.Trim();
        var visible = allNodes.Where(node => (!approvedOnly.Checked || node.Approved) &&
            node.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        count.Text = $"显示 {visible.Length} / 共 {allNodes.Count} 个节点  ·  {allNodes.Count(node => node.Approved)} 个已批准";
        if (visible.Length == 0 && allNodes.Count > 0) count.Text += "  ·  无匹配节点";
        clear.Enabled = search.Text.Length > 0 || approvedOnly.Checked;
        approvedOnly.ForeColor = approvedOnly.Checked ? Blue : Ink;
        var next = visible.Select(node => new RowState(node.Name, node.Type, node.Approved, node.Eligible,
            node.CanApprove, node.Reason ?? "", node.Name == activeNode)).ToArray();
        // A timer tick with identical data must leave rows, header sort and selection alone.
        if (rendered.SequenceEqual(next)) return;

        var chosen = Grid.SelectedRows.Count > 0 ? (Grid.SelectedRows[0].Tag as NodeSummary)?.Name : null;
        var firstIndex = Grid.FirstDisplayedScrollingRowIndex;
        var firstName = firstIndex >= 0 ? (Grid.Rows[firstIndex].Tag as NodeSummary)?.Name : null;
        var horizontalOffset = Grid.HorizontalScrollingOffset;
        var sortColumn = Grid.SortedColumn;
        var direction = Grid.SortOrder;
        var focusedColumn = Grid.CurrentCell?.OwningColumn;
        Grid.SuspendLayout();
        try
        {
            Grid.Rows.Clear();
            foreach (var node in visible)
            {
                var active = node.Name == activeNode;
                var availability = active ? "使用中" : node.Eligible ? node.Approved ? "可选择" : "待批准" : "身份不符";
                var index = Grid.Rows.Add(node.Name, node.Type, node.Approved ? "已批准" : "未批准", availability);
                var row = Grid.Rows[index]; row.Tag = node;
                row.Cells["Availability"].Style.ForeColor = active ? Green : node.Eligible ? Muted : Red;
                row.Cells["Approval"].Style.ForeColor = node.Approved ? Green : Muted;
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = node.Reason ?? "";
            }
            // New/changed data and filters rebuild rows; explicitly restore the user's header sort.
            if (sortColumn is not null && direction != SortOrder.None)
                Grid.Sort(sortColumn, direction == SortOrder.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending);
            Grid.CurrentCell = null; Grid.ClearSelection();
            var chosenRow = Grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => (row.Tag as NodeSummary)?.Name == chosen);
            if (chosenRow is not null)
            {
                Grid.CurrentCell = chosenRow.Cells[focusedColumn?.Index ?? 0]; chosenRow.Selected = true;
            }
            if (Grid.Rows.Count > 0)
            {
                var firstRow = !resetScroll && firstName is not null
                    ? Grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => (row.Tag as NodeSummary)?.Name == firstName) : null;
                Grid.FirstDisplayedScrollingRowIndex = resetScroll ? 0 : firstRow?.Index ?? Math.Clamp(firstIndex, 0, Grid.Rows.Count - 1);
                Grid.HorizontalScrollingOffset = horizontalOffset;
            }
            rendered = next;
        }
        finally { Grid.ResumeLayout(); }
    }

    private sealed record RowState(string Name, string Type, bool Approved, bool Eligible, bool CanApprove, string Reason, bool Active);
}
