using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using ClaudeSafe.Manager;

internal static class NodeListViewChecks
{
    private static NodeSummary Node(string name, bool approved = true, bool eligible = true, string type = "vless", string reason = "") =>
        new() { Name = name, Type = type, Approved = approved, Eligible = eligible, CanApprove = eligible, Reason = reason };

    private static List<NodeSummary> Nodes(string reason = "") =>
    [
        Node("SG Zebra", reason: reason), Node("JP Alpha", false, type: "trojan"),
        Node("sg Beta", type: "ss"), Node("SG Changed", false, false), Node("新加坡测试", type: "trojan")
    ];

    private static string[] Names(NodeListView view) => view.Grid.Rows.Cast<DataGridViewRow>()
        .Select(row => ((NodeSummary)row.Tag!).Name).ToArray();
    private static void Assert(bool value) { if (!value) throw new Exception("Node list regression failed."); }

    private static (Form Host, NodeListView View) CreateView()
    {
        var host = new Form { ClientSize = new Size(730, 430), Font = new Font("Microsoft YaHei UI", 9F), BackColor = Color.White };
        var view = new NodeListView { Padding = new Padding(12) };
        host.Controls.Add(view);
        _ = host.Handle; _ = view.Grid.Handle;
        host.PerformLayout(); view.PerformLayout();
        view.SetNodes(Nodes(), "SG Zebra");
        return (host, view);
    }

    public static void Run(Action<string, Action> check) => OnUiThread(() =>
    {
        check("node list keeps every column sort across repeated timer snapshots and changed data", () =>
        {
            var (host, view) = CreateView(); using (host)
            foreach (DataGridViewColumn column in view.Grid.Columns)
            foreach (var direction in new[] { ListSortDirection.Ascending, ListSortDirection.Descending })
            {
                view.Grid.Sort(column, direction);
                var expected = view.Grid.Rows.Cast<DataGridViewRow>().Select(row => row.Cells[column.Index].Value?.ToString()).ToArray();
                var firstRow = view.Grid.Rows[0];
                for (int i = 0; i < 4; i++) view.SetNodes(Nodes(), "SG Zebra");
                Assert(ReferenceEquals(firstRow, view.Grid.Rows[0]));
                view.SetNodes(Nodes("new source details"), "SG Zebra");
                Assert(view.Grid.SortedColumn == column && view.Grid.SortOrder ==
                    (direction == ListSortDirection.Ascending ? SortOrder.Ascending : SortOrder.Descending));
                Assert(expected.SequenceEqual(view.Grid.Rows.Cast<DataGridViewRow>().Select(row => row.Cells[column.Index].Value?.ToString())));
                view.SetNodes(Nodes(), "SG Zebra");
            }
        });
        check("name filter matches literal substrings case insensitively including Chinese", () =>
        {
            var (host, view) = CreateView(); using (host)
            {
                view.NameFilter = "  sG  "; Assert(Names(view).Length == 3 && Names(view).All(n => n.Contains("sg", StringComparison.OrdinalIgnoreCase)));
                view.NameFilter = "新加坡"; Assert(Names(view).SequenceEqual(["新加坡测试"]));
                view.SetNodes([Node("SG [A].*"), Node("SG Beta")], "REJECT");
                view.NameFilter = ".*"; Assert(Names(view).SequenceEqual(["SG [A].*"]));
            }
        });
        check("approved-only toggle combines with name filtering without changing approvals", () =>
        {
            var (host, view) = CreateView(); using (host)
            {
                var source = Nodes(); view.SetNodes(source, "SG Zebra"); view.ApprovedOnly = true;
                Assert(Names(view).Length == 3 && view.Grid.Rows.Cast<DataGridViewRow>().All(row => ((NodeSummary)row.Tag!).Approved));
                view.NameFilter = "SG"; Assert(Names(view).Length == 2);
                for (int i = 0; i < 3; i++) view.SetNodes(Nodes(), "SG Zebra");
                Assert(view.ApprovedOnly && view.NameFilter == "SG" && Names(view).Length == 2);
                Assert(source.Count == 5 && source.Count(node => node.Approved) == 3 && !source[1].Approved);
            }
        });
        check("filtering and clearing preserve the selected header sort", () =>
        {
            var (host, view) = CreateView(); using (host)
            {
                view.Grid.Sort(view.Grid.Columns["NodeName"]!, ListSortDirection.Descending);
                var expected = Names(view);
                view.NameFilter = "SG"; view.ApprovedOnly = true;
                Assert(Names(view).SequenceEqual(expected.Where(name => name is "SG Zebra" or "sg Beta")));
                view.ClearFilters(); Assert(Names(view).SequenceEqual(expected));
                Assert(view.NameFilter == "" && !view.ApprovedOnly && view.Grid.SortOrder == SortOrder.Descending);
            }
        });
        check("selected node survives a changed snapshot while filtered-out selection stays cleared", () =>
        {
            var (host, view) = CreateView(); using (host)
            {
                var chosen = view.Grid.Rows.Cast<DataGridViewRow>().Single(row => ((NodeSummary)row.Tag!).Name == "sg Beta");
                view.Grid.CurrentCell = chosen.Cells[0]; chosen.Selected = true;
                view.Grid.Sort(view.Grid.Columns["NodeName"]!, ListSortDirection.Descending);
                view.SetNodes(Nodes("updated reason"), "sg Beta");
                Assert(((NodeSummary)view.Grid.SelectedRows[0].Tag!).Name == "sg Beta");
                Assert(view.Grid.SelectedRows[0].Cells["Availability"].Value?.ToString() == "使用中");
                view.NameFilter = "JP";
                Assert(view.Grid.SelectedRows.Count == 0 && view.Grid.CurrentCell == null);
                view.ClearFilters(); Assert(view.Grid.SelectedRows.Count == 0);
            }
        });
        check("removed nodes and empty matches cannot leave a stale actionable selection", () =>
        {
            var (host, view) = CreateView(); using (host)
            {
                view.Grid.CurrentCell = view.Grid.Rows[0].Cells[0]; view.Grid.Rows[0].Selected = true;
                view.NameFilter = "no match";
                Assert(view.Grid.Rows.Count == 0 && view.Grid.SelectedRows.Count == 0 && view.CountText.Contains("无匹配节点"));
                view.ClearFilters(); view.SetNodes([], "REJECT");
                Assert(view.Grid.Rows.Count == 0 && view.Grid.CurrentCell == null && view.CountText.Contains("显示 0 / 共 0"));
            }
        });
        check("source and approval changes recompute an active filter and visible counts", () =>
        {
            var (host, view) = CreateView(); using (host)
            {
                view.NameFilter = "SG"; view.ApprovedOnly = true;
                view.SetNodes([Node("SG Added"), Node("SG Zebra", false), Node("JP Other")], "REJECT");
                Assert(Names(view).SequenceEqual(["SG Added"]));
                Assert(view.CountText.Contains("显示 1 / 共 3") && view.CountText.Contains("2 个已批准"));
                view.SetNodes([Node("SG Added", false), Node("SG Zebra", false)], "REJECT");
                Assert(view.Grid.Rows.Count == 0 && view.ApprovedOnly && view.NameFilter == "SG");
            }
        });
        check("changed snapshots retain the visible top node and selected identity after sorting", () =>
        {
            var (host, view) = CreateView(); using (host)
            {
                var many = Enumerable.Range(1, 30).Select(i => Node($"SG {i:00}")).ToArray();
                view.SetNodes(many, "REJECT"); view.Grid.Sort(view.Grid.Columns["NodeName"]!, ListSortDirection.Descending);
                view.Grid.CurrentCell = view.Grid.Rows[12].Cells[0]; view.Grid.Rows[12].Selected = true;
                var selected = ((NodeSummary)view.Grid.SelectedRows[0].Tag!).Name;
                view.Grid.FirstDisplayedScrollingRowIndex = 8;
                var top = ((NodeSummary)view.Grid.Rows[view.Grid.FirstDisplayedScrollingRowIndex].Tag!).Name;
                view.SetNodes(many.Select(node => Node(node.Name, reason: "refreshed")).ToArray(), "REJECT");
                Assert(((NodeSummary)view.Grid.SelectedRows[0].Tag!).Name == selected);
                Assert(((NodeSummary)view.Grid.Rows[view.Grid.FirstDisplayedScrollingRowIndex].Tag!).Name == top);
            }
        });
    });

    public static void SavePreview(string path) => OnUiThread(() =>
    {
        var (host, view) = CreateView(); using (host)
        {
            host.StartPosition = FormStartPosition.Manual; host.Location = new Point(-30000, -30000);
            host.ShowInTaskbar = false; host.Text = "ClaudeSafe · 节点筛选预览";
            host.Show();
            view.NameFilter = "SG"; view.ApprovedOnly = true;
            view.Grid.Sort(view.Grid.Columns["NodeName"]!, ListSortDirection.Descending);
            host.PerformLayout(); view.PerformLayout();
            using var bitmap = new Bitmap(host.Width, host.Height);
            host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            var file = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            bitmap.Save(file, ImageFormat.Png);
            host.Hide();
        }
    });

    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Application.EnableVisualStyles(); action(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Node list checks did not finish.");
        if (failure is not null) throw new Exception("Node list UI checks failed.", failure);
    }
}
