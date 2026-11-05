using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeSafe.Manager;

/// <summary>Reads active leaves without starting a manager, migrating files, or making network requests.</summary>
public static class SubscriptionSourceReader
{
    public static List<JsonObject> LoadActiveNodes(string dataDirectory)
    {
        var directory = Path.GetFullPath(dataDirectory);
        var catalogPath = Path.Combine(directory, "subscriptions.json");
        if (SubscriptionCatalog.Exists(catalogPath))
        {
            var catalog = SubscriptionCatalog.Read(catalogPath);
            var active = catalog.Items.SingleOrDefault(item => item.Id == catalog.ActiveId);
            return active is null ? [] : SubscriptionCatalog.CloneNodes(active.Nodes);
        }
        var legacyPath = Path.Combine(directory, "source.json");
        if (!File.Exists(legacyPath)) return [];
        try
        {
            if (new FileInfo(legacyPath).Length > CoreConfig.MaximumSourceBytes)
                throw new SafeOperationException("旧节点配置超过 16 MB 上限。");
            return CoreConfig.ParseSource(File.ReadAllText(legacyPath, new UTF8Encoding(false, true)));
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("旧节点配置无法读取；未修改本机数据。"); }
    }
}
