using System.Text.Json;

namespace ClaudeSafe.Manager;

public static class DesktopBenchmarkTargets
{
    public const string ObservedDate = "2026-10-25";
    public static List<BenchmarkTarget> Default() =>
    [
        new() { Id = "desktop-module-fatal-error", Label = "界面模块 JS", Kind = "resource", ExpectedType = "text/javascript", Url = "https://assets-proxy.anthropic.com/claude-ai/v2/assets/v1/cf52a4cc1-DUAN-HRf.js" },
        new() { Id = "desktop-module-partial-download", Label = "较大模块 JS", Kind = "resource", ExpectedType = "text/javascript", Url = "https://assets-proxy.anthropic.com/claude-ai/v2/assets/v1/c63275c0a-COI2XPdw.js" },
        new() { Id = "desktop-stylesheet", Label = "样式 CSS", Kind = "resource", ExpectedType = "text/css", Url = "https://assets-proxy.anthropic.com/claude-ai/v2/assets/v1/c3fde7009-BszyXGym.css" },
        new() { Id = "api.anthropic.com", Label = "API 匿名入口", Kind = "endpoint", Url = "https://api.anthropic.com/" }
    ];

    public static string[] Protocols(BenchmarkPreferences options) => options.Protocol switch
    {
        "http/1.1" => ["http/1.1"], "h2" => ["h2"], "both" => ["http/1.1", "h2"],
        _ => throw new SafeOperationException("测速协议无效。")
    };

    public static void ValidatePreferences(BenchmarkPreferences options)
    {
        if (!double.IsFinite(options.IntervalHours) || options.IntervalHours < 0.1 || options.IntervalHours > 8760 ||
            options.Rounds is < 1 or > 10 || options.TimeoutSeconds is < 3 or > 60 || options.Concurrency is < 1 or > 4)
            throw new SafeOperationException("测速参数超出范围：间隔 0.1–8760 小时，轮数 1–10，超时 3–60 秒，并发 1–4。");
        _ = Protocols(options);
    }

    public static List<BenchmarkTarget> Load(string dataDirectory)
    {
        var file = Path.Combine(dataDirectory, "desktop-targets.json");
        if (!File.Exists(file)) return Default();
        try
        {
            if (new FileInfo(file).Length > 64 * 1024) throw new FormatException();
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var element = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("targets");
            var targets = JsonSerializer.Deserialize<List<BenchmarkTarget>>(element.GetRawText(), ManagerJson.Options) ?? throw new FormatException();
            // A maintenance override may refresh the three public asset URLs, not remove failed targets.
            var defaults = Default();
            // Upgrade only the explicitly retired homepage entry. Required Desktop assets/API remain mandatory.
            if (targets.Count == defaults.Count + 1 && targets.Count(IsLegacyHomepage) == 1)
                targets = targets.Where(t => !IsLegacyHomepage(t)).ToList();
            if (targets.Count != defaults.Count) throw new FormatException();
            foreach (var required in defaults)
            {
                var matches = targets.Where(t => t.Id == required.Id).ToArray();
                if (matches.Length != 1 || matches[0].Kind != required.Kind || matches[0].ExpectedType != required.ExpectedType ||
                    (required.Kind == "endpoint" && matches[0].Url != required.Url)) throw new FormatException();
                Validate(matches[0]);
            }
            return targets;
        }
        catch { throw new SafeOperationException("Desktop 测速目标清单无效。仅允许更新原有三个公开资源 URL，不能删减必测项。"); }
    }

    public static bool IsLegacyHomepage(BenchmarkTarget target) => target.Id == "claude.ai" && target.Kind == "endpoint" &&
        target.Url == "https://claude.ai/" && string.IsNullOrEmpty(target.ExpectedType);

    public static Uri Validate(BenchmarkTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Id) || target.Id.Length > 100 || target.Id.Any(char.IsControl) ||
            !Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new SafeOperationException("Desktop 测速目标地址不受支持。");
        if (target.Kind == "resource")
        {
            if (uri.Host != "assets-proxy.anthropic.com" || !uri.AbsolutePath.StartsWith("/claude-ai/v2/assets/v1/", StringComparison.Ordinal) ||
                !(target.ExpectedType == "text/javascript" && uri.AbsolutePath.EndsWith(".js", StringComparison.Ordinal) ||
                  target.ExpectedType == "text/css" && uri.AbsolutePath.EndsWith(".css", StringComparison.Ordinal)))
                throw new SafeOperationException("Desktop 资源地址必须为批准 CDN 下的 JS 或 CSS。");
        }
        else if (target.Kind != "endpoint" || uri.AbsolutePath != "/" ||
                 uri.Host is not ("claude.ai" or "api.anthropic.com"))
            throw new SafeOperationException("测速入口只允许 Claude 和 API 的匿名根地址。");
        return uri;
    }
}
