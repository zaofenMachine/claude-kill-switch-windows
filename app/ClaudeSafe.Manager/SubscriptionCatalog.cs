using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSafe.Manager;

internal static class SubscriptionCatalog
{
    public const int MaximumProfiles = 32;
    public const int MaximumCatalogBytes = 64 * 1024 * 1024;
    public const int MaximumNameLength = 80;
    public const int MaximumUrlLength = 8192;

    public static bool Exists(string path)
    {
        // File.Exists hides access and path errors as "absent", which could select stale legacy data.
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new SafeOperationException("subscriptions.json 是目录，已拒绝回退到旧节点来源。请检查本机数据。");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("无法确认 subscriptions.json 的状态，已拒绝回退到旧节点来源。请检查本机数据权限。"); }
    }

    public static string ValidateName(string? name)
    {
        var value = (name ?? "").Trim();
        if (value.Length is 0 or > MaximumNameLength || value.Any(char.IsControl))
            throw new SafeOperationException("订阅名称须为 1–80 个字符，且不能包含控制字符。");
        return value;
    }

    public static Uri ValidateUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaximumUrlLength || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || string.IsNullOrEmpty(uri.Host))
            throw new SafeOperationException("请输入不含用户名、空白或片段的 HTTPS 订阅地址（最多 8192 个字符）。");
        return uri;
    }

    public static string MaskedUrl(StoredSubscription item) => item.Kind == "https"
        ? "https://" + ValidateUrl(item.Url).IdnHost + "/…"
        : "本地文件快照";

    public static string ErrorMessage(string code) => code switch
    {
        "http" => "服务器未返回成功响应；不跟随重定向。原节点缓存已保留。",
        "large" => "订阅超过 16 MB 上限。原节点缓存已保留。",
        "format" => "订阅不是有效的 UTF-8 Mihomo YAML/JSON 节点配置。原节点缓存已保留。",
        "download" => "订阅下载失败或超时。请检查系统代理、网络及订阅地址；原节点缓存已保留。",
        _ => ""
    };

    public static List<JsonObject> CloneNodes(IEnumerable<JsonObject> nodes) =>
        nodes.Select(node => (JsonObject)node.DeepClone()).ToList();

    public static SubscriptionCatalogState Clone(SubscriptionCatalogState source) => new()
    {
        Version = source.Version, ActiveId = source.ActiveId,
        Items = source.Items.Select(item => new StoredSubscription
        {
            Id = item.Id, Name = item.Name, Kind = item.Kind, Url = item.Url, Revision = item.Revision,
            LastUpdatedUtc = item.LastUpdatedUtc, LastErrorCode = item.LastErrorCode, Nodes = CloneNodes(item.Nodes)
        }).ToList()
    };

    public static bool SameNodes(IReadOnlyCollection<JsonObject> a, IReadOnlyCollection<JsonObject> b)
    {
        if (a.Count != b.Count) return false;
        var byName = b.ToDictionary(node => CoreConfig.Identity(node).Name, StringComparer.Ordinal);
        return a.All(node => byName.TryGetValue(CoreConfig.Identity(node).Name, out var other) && JsonNode.DeepEquals(node, other));
    }

    public static string SourceText(IEnumerable<JsonObject> nodes) => new JsonObject
    {
        ["proxies"] = new JsonArray(nodes.Select(node => node.DeepClone()).ToArray())
    }.ToJsonString(ManagerJson.Options);

    public static SubscriptionCatalogState Read(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaximumCatalogBytes) throw new FormatException();
            var text = File.ReadAllText(path, new UTF8Encoding(false, true));
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateFields(document.RootElement);
            var state = JsonSerializer.Deserialize<SubscriptionCatalogState>(text, ManagerJson.Options) ?? throw new FormatException();
            Validate(state);
            return state;
        }
        catch { throw new SafeOperationException("subscriptions.json 损坏或超出上限；不会回退或覆盖旧节点来源。请从备份恢复。"); }
    }

    private static void RejectDuplicateFields(JsonElement value, bool catalogueFields = true)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(catalogueFields ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new FormatException();
                RejectDuplicateFields(property.Value, catalogueFields && !property.Name.Equals("nodes", StringComparison.OrdinalIgnoreCase));
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RejectDuplicateFields(child, catalogueFields);
    }

    public static string Serialize(SubscriptionCatalogState state)
    {
        Validate(state);
        var text = JsonSerializer.Serialize(state, ManagerJson.Options);
        if (Encoding.UTF8.GetByteCount(text) > MaximumCatalogBytes)
            throw new SafeOperationException("订阅缓存总大小超过 64 MB；请移除不再需要的订阅后重试。");
        return text;
    }

    private static void Validate(SubscriptionCatalogState state)
    {
        if (state.Version != 1 || state.Items is null || state.Items.Count > MaximumProfiles || state.ActiveId is null)
            throw new SafeOperationException("订阅目录版本或条目数无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long approximateBytes = 0;
        foreach (var item in state.Items)
        {
            if (item is null || !Guid.TryParseExact(item.Id, "N", out _) || !ids.Add(item.Id) ||
                item.Name != ValidateName(item.Name) || !names.Add(item.Name) ||
                !Guid.TryParseExact(item.Revision, "N", out _) || item.Nodes is null ||
                item.LastErrorCode is not ("" or "http" or "large" or "format" or "download"))
                throw new SafeOperationException("订阅条目无效、名称重复或错误状态无效。");
            if (item.Kind == "https") _ = ValidateUrl(item.Url);
            else if (item.Kind != "file" || item.Url != "") throw new SafeOperationException("订阅类型无效。");
            var serialized = SourceText(item.Nodes);
            approximateBytes += Encoding.UTF8.GetByteCount(serialized) + Encoding.UTF8.GetByteCount(item.Url) + 1024;
            if (approximateBytes > MaximumCatalogBytes)
                throw new SafeOperationException("订阅缓存总大小超过 64 MB；请移除不再需要的订阅后重试。");
            // Reuse the exact node parser used for one-shot imports, including identity and duplicate validation.
            _ = CoreConfig.ParseSource(serialized);
        }
        if (state.ActiveId.Length > 0 && !ids.Contains(state.ActiveId))
            throw new SafeOperationException("当前订阅不存在，已拒绝读取订阅目录。");
    }
}
