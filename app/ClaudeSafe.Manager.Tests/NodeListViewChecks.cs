using ClaudeSafe.Manager;

internal static class NodeListViewChecks
{
    private static NodeSummary Node(string name, bool approved = true, bool eligible = true, string type = "vless", string reason = "") =>
        new() { Name = name, Type = type, Approved = approved, Eligible = eligible, CanApprove = eligible, Reason = reason };
    private static List<NodeSummary> Nodes(string reason = "") =>
    [Node("SG Zebra", reason: reason), Node("JP Alpha", false, type: "trojan"), Node("sg Beta", type: "ss"), Node("SG Changed", false, false), Node("新加坡测试", type: "trojan")];
    private static NodeListState Create() { var state = new NodeListState(); state.SetNodes(Nodes(), "SG Zebra"); return state; }
    private static string[] Names(NodeListState state) => state.Rows.Select(row => row.Name).ToArray();
    private static void Assert(bool value) { if (!value) throw new Exception("Node list regression failed."); }

    public static void Run(Action<string, Action> check)
    {
        check("node list preserves every column sort and row identity during timer refresh", () =>
        {
            foreach (var column in Enum.GetValues<NodeSortColumn>())
            foreach (var descending in new[] { false, true })
            {
                var state = Create(); state.Sort(column, descending);
                var expected = Names(state); var rows = state.Rows.ToArray();
                for (var i = 0; i < 4; i++) state.SetNodes(Nodes(), "SG Zebra");
                state.SetNodes(Nodes("new source details"), "SG Zebra");
                Assert(state.SortColumn == column && state.Descending == descending && Names(state).SequenceEqual(expected));
                Assert(rows.Zip(state.Rows).All(pair => ReferenceEquals(pair.First, pair.Second)));
            }
        });
        check("node name filtering is literal case insensitive and supports Chinese", () =>
        {
            var state = Create(); state.NameFilter = "  sG  "; state.Refresh(); Assert(state.Rows.Count == 3);
            state.NameFilter = "新加坡"; state.Refresh(); Assert(Names(state).SequenceEqual(["新加坡测试"]));
            state.SetNodes([Node("SG [A].*"), Node("SG Beta")], "REJECT"); state.NameFilter = ".*"; state.Refresh();
            Assert(Names(state).SequenceEqual(["SG [A].*"]));
        });
        check("approved-only filter combines with name without modifying source approvals", () =>
        {
            var source = Nodes(); var state = Create(); state.SetNodes(source, "SG Zebra");
            state.ApprovedOnly = true; state.Refresh(); Assert(state.Rows.Count == 3 && state.Rows.All(row => row.Node.Approved));
            state.NameFilter = "SG"; state.Refresh(); Assert(state.Rows.Count == 2);
            for (var i = 0; i < 3; i++) state.SetNodes(Nodes(), "SG Zebra");
            Assert(state.ApprovedOnly && state.NameFilter == "SG" && state.Rows.Count == 2 && source.Count == 5 && source.Count(node => node.Approved) == 3);
        });
        check("filtering and clearing retain the chosen column sort", () =>
        {
            var state = Create(); state.Sort(NodeSortColumn.Name, true); var expected = Names(state);
            state.NameFilter = "SG"; state.ApprovedOnly = true; state.Refresh();
            Assert(Names(state).SequenceEqual(expected.Where(name => name is "SG Zebra" or "sg Beta")));
            state.ClearFilters(); Assert(Names(state).SequenceEqual(expected) && state.Descending && !state.ApprovedOnly && state.NameFilter == "");
        });
        check("selection survives updated snapshots but never reappears after being filtered out", () =>
        {
            var state = Create(); state.SelectedName = "sg Beta"; state.Sort(NodeSortColumn.Name, true);
            state.SetNodes(Nodes("updated reason"), "sg Beta");
            Assert(state.Selected?.Name == "sg Beta" && state.Rows.Single(row => row.Name == "sg Beta").Availability == "使用中");
            state.NameFilter = "JP"; state.Refresh(); Assert(state.Selected is null && state.SelectedName is null);
            state.ClearFilters(); Assert(state.Selected is null);
        });
        check("removed and filtered nodes cannot leave stale actionable selections", () =>
        {
            var state = Create(); state.SelectedName = state.Rows[0].Name; state.NameFilter = "no match"; state.Refresh();
            Assert(state.Rows.Count == 0 && state.Selected is null && state.CountText.Contains("无匹配节点"));
            state.ClearFilters(); state.SelectedName = state.Rows[0].Name; state.SetNodes([], "REJECT");
            Assert(state.Rows.Count == 0 && state.SelectedName is null && state.CountText.Contains("显示 0 / 共 0"));
        });
        check("approval and source updates recalculate active filters and counts", () =>
        {
            var state = Create(); state.NameFilter = "SG"; state.ApprovedOnly = true;
            state.SetNodes([Node("SG Added"), Node("SG Zebra", false), Node("JP Other")], "REJECT");
            Assert(Names(state).SequenceEqual(["SG Added"]) && state.CountText.Contains("显示 1 / 共 3") && state.CountText.Contains("2 个已批准"));
            state.SetNodes([Node("SG Added", false), Node("SG Zebra", false)], "REJECT");
            Assert(state.Rows.Count == 0 && state.ApprovedOnly && state.NameFilter == "SG");
        });
        check("changed row data notifies WinUI without replacing the observable list or moving unchanged rows", () =>
        {
            var state = Create(); var many = Enumerable.Range(1, 30).Select(i => Node($"SG {i:00}")).ToArray();
            state.SetNodes(many, "REJECT"); state.Sort(NodeSortColumn.Name, true); state.SelectedName = state.Rows[12].Name;
            var list = state.Rows; var top = state.Rows[8]; var notifications = 0; var moves = 0;
            top.PropertyChanged += (_, _) => notifications++; state.Rows.CollectionChanged += (_, _) => moves++;
            state.SetNodes(many.Select(node => Node(node.Name, reason: "refreshed")).ToArray(), "REJECT");
            Assert(ReferenceEquals(list, state.Rows) && ReferenceEquals(top, state.Rows[8]) && state.SelectedName == state.Rows[12].Name && notifications == 1 && moves == 0);
        });
    }
}
