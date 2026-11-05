using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ClaudeSafe.Manager;

public enum NodeSortColumn { Name, Protocol, Approval, Availability }

/// <summary>Presentation state only; never mutates source, approvals or the route.</summary>
public sealed class NodeListState
{
    private IReadOnlyList<NodeSummary> source = [];
    private string active = "REJECT";
    public ObservableCollection<NodeRow> Rows { get; } = [];
    public string NameFilter { get; set; } = "";
    public bool ApprovedOnly { get; set; }
    public NodeSortColumn? SortColumn { get; private set; }
    public bool Descending { get; private set; }
    public string? SelectedName { get; set; }
    public string CountText { get; private set; } = "显示 0 / 共 0 个节点";
    public NodeSummary? Selected => Rows.FirstOrDefault(row => row.Name == SelectedName)?.Node;
    public void SetNodes(IReadOnlyList<NodeSummary> nodes, string activeNode) { source = nodes; active = activeNode; Refresh(); }
    public void Sort(NodeSortColumn column, bool descending) { SortColumn = column; Descending = descending; Refresh(); }
    public void ClearFilters() { NameFilter = ""; ApprovedOnly = false; Refresh(); }

    public void Refresh()
    {
        var term = NameFilter.Trim();
        var visible = source.Where(node => (!ApprovedOnly || node.Approved) && node.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (SortColumn is { } column)
        {
            string Key(NodeSummary node) => column switch
            {
                NodeSortColumn.Protocol => node.Type,
                NodeSortColumn.Approval => node.Approved ? "已批准" : "未批准",
                NodeSortColumn.Availability => NodeRow.AvailabilityOf(node, active),
                _ => node.Name
            };
            visible = Descending ? visible.OrderByDescending(Key, StringComparer.CurrentCultureIgnoreCase).ThenBy(node => node.Name, StringComparer.Ordinal)
                : visible.OrderBy(Key, StringComparer.CurrentCultureIgnoreCase).ThenBy(node => node.Name, StringComparer.Ordinal);
        }
        var next = visible.ToArray();
        var names = next.Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = Rows.Count - 1; i >= 0; i--) if (!names.Contains(Rows[i].Name)) Rows.RemoveAt(i);
        for (var i = 0; i < next.Length; i++)
        {
            var node = next[i];
            var row = Rows.FirstOrDefault(item => item.Name == node.Name);
            if (row is null) { row = new NodeRow(node, active); Rows.Insert(i, row); }
            else { var oldIndex = Rows.IndexOf(row); if (oldIndex != i) Rows.Move(oldIndex, i); row.Update(node, active); }
        }
        if (SelectedName is not null && !names.Contains(SelectedName)) SelectedName = null;
        CountText = $"显示 {next.Length} / 共 {source.Count} 个节点  ·  {source.Count(node => node.Approved)} 个已批准";
        if (next.Length == 0 && source.Count > 0) CountText += "  ·  无匹配节点";
    }
}

public sealed class NodeRow : INotifyPropertyChanged
{
    public NodeSummary Node { get; private set; }
    private string active;
    public string Name => Node.Name;
    public string Protocol => Node.Type;
    public string Approval => Node.Approved ? "已批准" : "未批准";
    public string Availability => AvailabilityOf(Node, active);
    public string Reason => Node.Reason;
    public bool IsActive => Name == active;
    public override string ToString() => $"{Name}，{Protocol}，{Approval}，{Availability}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public NodeRow(NodeSummary node, string activeNode) { Node = node; active = activeNode; }
    public void Update(NodeSummary node, string activeNode)
    {
        var changed = Node.Type != node.Type || Node.Approved != node.Approved || Node.Eligible != node.Eligible ||
            Node.CanApprove != node.CanApprove || Node.Reason != node.Reason || active != activeNode;
        Node = node; active = activeNode;
        if (changed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    public static string AvailabilityOf(NodeSummary node, string activeNode) => node.Name == activeNode ? "使用中"
        : node.Eligible ? node.Approved ? "可选择" : "待批准" : "身份不符";
}
