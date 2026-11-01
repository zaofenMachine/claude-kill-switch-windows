using System.Text;

namespace ClaudeSafe.Manager;

// An explicit, one-time update command can restore an already-approved selection.
// Ordinary launches have no startup request and still leave the core stopped/REJECT.
public static class ManagerStartupRequest
{
    public static string DecodeApprovedNode(string encoded, ManagerSnapshot snapshot)
    {
        try
        {
            if (encoded.Length > 2048) throw new FormatException();
            string name = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
            if (name.Length is < 1 or > 200 || name.Any(char.IsControl) ||
                name != "REJECT" && !snapshot.Nodes.Any(n => n.Name == name && n.Approved && n.Eligible)) throw new FormatException();
            return name;
        }
        catch { throw new SafeOperationException("更新恢复请求不是当前有效的批准节点，通道保持阻断。"); }
    }

    public static async Task RestoreAsync(ManagerService manager, string encoded)
    {
        try
        {
            var name = DecodeApprovedNode(encoded, await manager.SnapshotAsync().ConfigureAwait(false));
            await manager.StartAsync().ConfigureAwait(false); // Starts as REJECT and verifies core/ports before selecting.
            await manager.SelectAsync(name).ConfigureAwait(false);
        }
        catch
        {
            try { await manager.StopAsync().ConfigureAwait(false); } catch { }
            throw new SafeOperationException("未能安全恢复更新前的节点，专用通道已停止。请手动启动并选择批准节点。");
        }
    }
}
