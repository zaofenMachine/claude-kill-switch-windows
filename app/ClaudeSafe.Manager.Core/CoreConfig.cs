using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace ClaudeSafe.Manager;

/// <summary>Build from leaf objects only. Never execute or merge a subscription's routing configuration.</summary>
public static class CoreConfig
{
    public const int ProxyPort = 17987;
    public const int ControllerPort = 17990;
    public const int MaximumSourceBytes = 16 * 1024 * 1024;
    public const string Group = "Claude-Safe";
    public static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    { "DIRECT", "REJECT", "REJECT-DROP", "PASS", "COMPATIBLE", "GLOBAL", Group };
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    { "vless", "vmess", "trojan", "ss", "ssr", "hysteria", "hysteria2", "tuic", "anytls", "socks5", "http" };
    // Unsupported options remain visible as ineligible rather than being silently discarded.
    private static readonly HashSet<string> LeafKeys = new(StringComparer.Ordinal)
    {
        "name", "type", "server", "port", "uuid", "password", "username", "cipher", "alterId", "udp",
        "tls", "servername", "sni", "network", "flow", "client-fingerprint", "fingerprint", "alpn",
        "reality-opts", "ws-opts", "http-opts", "h2-opts", "grpc-opts", "obfs", "obfs-password",
        "auth", "auth-str", "up", "down", "ports", "hop-interval", "reduce-rtt", "disable-sni",
        "token", "ip", "request-timeout", "udp-relay-mode", "congestion-controller", "fast-open",
        "heartbeat-interval", "disable-mtu-discovery", "protocol", "protocol-param", "obfs-param",
        "plugin", "plugin-opts", "tfo", "mptcp", "skip-cert-verify"
    };
    private static readonly HashSet<string> ForbiddenNestedKeys = new(StringComparer.OrdinalIgnoreCase)
    { "dialer-proxy", "interface-name", "routing-mark", "certificate", "private-key", "ca", "ca-str", "ech-opts", "path-to-cert", "path-to-key" };

    public static List<JsonObject> ParseSource(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumSourceBytes)
            throw new SafeOperationException("配置超过 16 MB 上限。");
        try
        {
            JsonNode? root;
            if (text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith('{'))
            {
                root = JsonNode.Parse(text.TrimStart('\uFEFF'), new JsonNodeOptions(),
                    new JsonDocumentOptions { MaxDepth = 24 });
                RejectDuplicateJsonKeys(text.TrimStart('\uFEFF'));
            }
            else
            {
                // Disallow aliases, multiple documents and custom tags; they are unnecessary for imported leaves.
                var parser = new Parser(new StringReader(text));
                int events = 0, depth = 0;
                while (parser.MoveNext())
                {
                    if (++events > 200000 || parser.Current is AnchorAlias) throw new FormatException();
                    if (parser.Current is NodeEvent tagged && !tagged.Tag.IsEmpty && !tagged.Tag.Value.StartsWith("tag:yaml.org,2002:")) throw new FormatException();
                    if (parser.Current is MappingStart or SequenceStart) { if (++depth > 24) throw new FormatException(); }
                    if (parser.Current is MappingEnd or SequenceEnd) depth--;
                }
                var stream = new YamlStream(); stream.Load(new StringReader(text));
                if (stream.Documents.Count != 1) throw new FormatException();
                root = ConvertYaml(stream.Documents[0].RootNode, 0);
            }
            if (root is not JsonObject obj || obj["proxies"] is not JsonArray array || array.Count > 4096)
                throw new FormatException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<JsonObject>();
            foreach (var item in array)
            {
                if (item is not JsonObject node) throw new FormatException();
                var pin = Identity(node);
                if (!names.Add(pin.Name)) throw new SafeOperationException("节点名称重复；请清理后重新导入。");
                result.Add((JsonObject)node.DeepClone());
            }
            return result;
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("无法读取节点列表。请选择含 proxies 数组的 YAML 或 JSON；不支持别名、重复字段或多个文档。"); }
    }

    private static void RejectDuplicateJsonKeys(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 24 });
        void Visit(JsonElement item)
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                { if (!keys.Add(property.Name)) throw new FormatException(); Visit(property.Value); }
            }
            else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) Visit(child);
        }
        Visit(doc.RootElement);
    }

    private static JsonNode? ConvertYaml(YamlNode value, int depth)
    {
        if (depth > 24 || (!value.Tag.IsEmpty && !value.Tag.Value.StartsWith("tag:yaml.org,2002:"))) throw new FormatException();
        if (value is YamlMappingNode mapping)
        {
            var result = new JsonObject();
            foreach (var pair in mapping.Children)
            {
                if (pair.Key is not YamlScalarNode key || key.Value is null || result.ContainsKey(key.Value)) throw new FormatException();
                result.Add(key.Value, ConvertYaml(pair.Value, depth + 1));
            }
            return result;
        }
        if (value is YamlSequenceNode sequence)
            return new JsonArray(sequence.Children.Select(child => ConvertYaml(child, depth + 1)).ToArray());
        if (value is not YamlScalarNode scalar) throw new FormatException();
        var s = scalar.Value ?? "";
        if (!scalar.Tag.IsEmpty && scalar.Tag.Value == "tag:yaml.org,2002:str") return JsonValue.Create(s);
        if (scalar.Style == ScalarStyle.Plain)
        {
            if (s is "null" or "Null" or "NULL" or "~") return null;
            if (bool.TryParse(s, out var flag)) return JsonValue.Create(flag);
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) && integer.ToString(CultureInfo.InvariantCulture) == s)
                return JsonValue.Create(integer);
        }
        return JsonValue.Create(s);
    }

    public static NodePin Identity(JsonObject node)
    {
        var name = String(node, "name"); var type = String(node, "type"); var server = String(node, "server");
        int port = 0;
        if (node["port"] is JsonValue val)
        {
            if (!val.TryGetValue<int>(out port) && !(val.TryGetValue<string>(out var s) && int.TryParse(s, out port)))
            { if (val.TryGetValue<long>(out var l) && l is > 0 and <= 65535) port = (int)l; }
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl) || ReservedNames.Contains(name) ||
            string.IsNullOrWhiteSpace(server) || server.Length > 253 || server.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "/\\#@?".Contains(c)) ||
            string.IsNullOrEmpty(type) || port is < 1 or > 65535)
            throw new SafeOperationException("节点身份字段无效，或使用了保留名称。");
        return new NodePin(name, type, server, port);
    }

    public static string String(JsonObject node, string key) =>
        node[key] is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";

    public static string IneligibleReason(JsonObject node)
    {
        NodePin pin;
        try { pin = Identity(node); } catch (SafeOperationException) { return "节点身份字段无效"; }
        if (!Types.Contains(pin.Type)) return "暂不支持该协议";
        if (node.Any(p => !LeafKeys.Contains(p.Key))) return "含不支持或可能改变路由的配置项";
        if (HasForbidden(node)) return "链式代理、接口覆盖或证书验证设置不受支持";
        if (pin.Server.Equals("localhost", StringComparison.OrdinalIgnoreCase) || pin.Server.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return "节点服务器不可为本机";
        if (IPAddress.TryParse(pin.Server, out var ip) && (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))) return "节点服务器不可为本机";
        return "";
    }

    private static bool HasForbidden(JsonNode? item)
    {
        if (item is JsonObject o)
        {
            foreach (var pair in o)
            {
                if (ForbiddenNestedKeys.Contains(pair.Key)) return true;
                if (pair.Key.Equals("skip-cert-verify", StringComparison.OrdinalIgnoreCase) && pair.Value is not null &&
                    !(pair.Value is JsonValue flag && flag.TryGetValue<bool>(out var boolean) && !boolean)) return true;
                if (HasForbidden(pair.Value)) return true;
            }
        }
        if (item is JsonArray a) return a.Any(HasForbidden);
        return false;
    }

    public static List<NodeSummary> Summaries(ManagerSettings settings, IReadOnlyList<JsonObject> nodes)
    {
        ValidatePins(settings.Pins);
        return nodes.Select(node =>
        {
            var identity = Identity(node); var pin = settings.Pins.SingleOrDefault(p => p.Name == identity.Name);
            var reason = IneligibleReason(node);
            bool canApprove = reason.Length == 0 && !identity.Name.Equals(settings.InterfaceName, StringComparison.Ordinal);
            if (pin is not null && pin != identity) reason = "来源身份已变化，需要重新批准";
            if (identity.Name.Equals(settings.InterfaceName, StringComparison.Ordinal)) reason = "节点名称与出站网卡冲突";
            return new NodeSummary { Name = identity.Name, Type = identity.Type, Approved = pin == identity,
                Eligible = reason.Length == 0, CanApprove = canApprove,
                Reason = reason.Length > 0 ? reason : pin == identity ? "已批准" : "待手动批准" };
        }).ToList();
    }

    public static void ValidatePins(IReadOnlyList<NodePin> pins)
    {
        if (pins.Count > 4096 || pins.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != pins.Count)
            throw new SafeOperationException("批准清单包含重复项或过多节点。");
        foreach (var pin in pins)
        {
            var node = JsonSerializer.SerializeToNode(pin, ManagerJson.Options)!.AsObject();
            if (Identity(node) != pin || !Types.Contains(pin.Type)) throw new SafeOperationException("批准清单无效。");
        }
    }

    public static JsonObject Build(ManagerSettings settings, IReadOnlyList<JsonObject> nodes, string secret)
    {
        ValidatePins(settings.Pins);
        if (string.IsNullOrWhiteSpace(settings.InterfaceName) || settings.InterfaceName.Any(char.IsControl) ||
            settings.InterfaceName.IndexOfAny(['#', '&', '?', '\r', '\n']) >= 0 || ReservedNames.Contains(settings.InterfaceName))
            throw new SafeOperationException("请先选择可用的真实出站网卡。");
        var summaries = Summaries(settings, nodes);
        var allowed = summaries.Where(n => n.Approved && n.Eligible).Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        var approved = nodes.Where(n => allowed.Contains(Identity(n).Name)).Select(n => n.DeepClone()).ToArray();
        var members = new JsonArray { "REJECT" };
        foreach (var name in allowed.Order(StringComparer.Ordinal)) members.Add(name);
        var dns = new JsonArray($"https://223.5.5.5/dns-query#{settings.InterfaceName}", $"https://1.12.12.12/dns-query#{settings.InterfaceName}");
        return new JsonObject
        {
            ["mixed-port"] = 0, ["port"] = 0, ["socks-port"] = 0, ["allow-lan"] = false,
            ["bind-address"] = "127.0.0.1", ["mode"] = "rule", ["log-level"] = "warning", ["ipv6"] = false,
            ["interface-name"] = settings.InterfaceName, ["external-controller"] = $"127.0.0.1:{ControllerPort}", ["secret"] = secret,
            ["profile"] = new JsonObject { ["store-selected"] = false, ["store-fake-ip"] = false },
            ["tun"] = new JsonObject { ["enable"] = false, ["auto-route"] = false, ["auto-detect-interface"] = false },
            ["dns"] = new JsonObject
            {
                ["enable"] = true, ["ipv6"] = false, ["enhanced-mode"] = "redir-host", ["use-hosts"] = false,
                ["use-system-hosts"] = false, ["respect-rules"] = false,
                ["default-nameserver"] = dns.DeepClone(), ["nameserver"] = dns.DeepClone(), ["proxy-server-nameserver"] = dns.DeepClone()
            },
            ["proxies"] = new JsonArray(approved),
            ["proxy-groups"] = new JsonArray(new JsonObject { ["name"] = Group, ["type"] = "select", ["proxies"] = members,
                ["default-selected"] = "REJECT", ["empty-fallback"] = "REJECT" }),
            ["listeners"] = new JsonArray(new JsonObject { ["name"] = "claude-safe", ["type"] = "mixed", ["listen"] = "127.0.0.1",
                ["port"] = ProxyPort, ["udp"] = false, ["proxy"] = Group }),
            ["rules"] = new JsonArray("MATCH,REJECT")
        };
    }

    public static string SerializeForCore(JsonObject configuration)
    {
        // Go YAML rejects JSON UTF-16 surrogate escapes emitted for flags/other supplementary-plane characters.
        // Serialize actual typed values as YAML; never replace textual \\u sequences inside credentials.
        object? ConvertValue(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertValue(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertValue).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var number) ? (object)number : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new SafeOperationException("候选配置包含无效的值。")
        };
        var serializer = new SerializerBuilder().DisableAliases().WithQuotingNecessaryStrings().Build();
        return serializer.Serialize(ConvertValue(JsonSerializer.SerializeToElement(configuration)));
    }
}
