using System.Text.Json;

namespace ClaudeSafe.Manager;

public sealed class BenchmarkPreferences
{
    public bool ScheduleEnabled { get; set; } = true;
    public double IntervalHours { get; set; } = 24;
    public bool AutoSwitchEnabled { get; set; }
    public bool StopOnFirstFailure { get; set; }
    public int Rounds { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 30;
    public int Concurrency { get; set; } = 2;
    public string Protocol { get; set; } = "both";
}

public sealed class BenchmarkTarget
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
    public string Kind { get; set; } = "endpoint";
    public string ExpectedType { get; set; } = "";
}

public sealed class BenchmarkSample
{
    public string Name { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Protocol { get; set; } = "http/1.1";
    public string ActualProtocol { get; set; } = "";
    public int Round { get; set; }
    public int Http { get; set; }
    public bool Complete { get; set; }
    public bool TlsVerified { get; set; }
    public bool ResourceValid { get; set; }
    public long Bytes { get; set; }
    public long? ExpectedBytes { get; set; }
    public double? TtfbMs { get; set; }
    public double TotalMs { get; set; }
    public string ContentType { get; set; } = "";
    public string Error { get; set; } = "";
    public string Status { get; set; } = "network_error";
    public DateTimeOffset AtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BenchmarkRank
{
    public string Name { get; set; } = "";
    public bool AllPassed { get; set; }
    public int PassedSlots { get; set; }
    public int CoveredSlots { get; set; }
    public int SkippedSlots { get; set; }
    public int ExpectedSlots { get; set; }
    public int PassedTargets { get; set; }
    public int TotalTargets { get; set; }
    public double? WorstMedianMs { get; set; }
    public double? MeanMedianMs { get; set; }
    public double? SlowestMs { get; set; }
}

public sealed class BenchmarkRun
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "idle";
    public string Trigger { get; set; } = "manual";
    public string Message { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public BenchmarkPreferences Options { get; set; } = new();
    public List<BenchmarkTarget> Targets { get; set; } = [];
    public List<string> NodeNames { get; set; } = [];
    public List<BenchmarkSample> Samples { get; set; } = [];
    public List<BenchmarkRank> Ranks { get; set; } = [];
    public int Completed { get; set; }
    public int Total { get; set; }
    public int Attempted => Samples.Count(s => !DesktopBenchmarkSamples.IsSkipped(s));
    public int Skipped => Samples.Count(DesktopBenchmarkSamples.IsSkipped);
    public bool Cancelled { get; set; }
    public string AutoSwitchOutcome { get; set; } = "";
}

public static class DesktopBenchmarkSamples
{
    public const string SkippedReason = "此前测试未通过，已按设置跳过此节点后续测试。";

    public static bool IsSkipped(BenchmarkSample sample) =>
        string.Equals(sample.Status, "skipped", StringComparison.OrdinalIgnoreCase);

    public static BenchmarkSample CreateSkipped(string name, BenchmarkTarget target, string protocol, int round) => new()
    {
        Name = name, TargetId = target.Id, Protocol = protocol, Round = round,
        Status = "skipped", Error = SkippedReason
    };

    // A skipped slot records the plan, never a request or a successful response.
    public static bool IsCanonicalSkipped(BenchmarkSample sample) => sample.Status == "skipped" &&
        sample.Error == SkippedReason && !sample.Complete && !sample.TlsVerified && !sample.ResourceValid &&
        sample.Http == 0 && sample.Bytes == 0 && sample.ExpectedBytes is null &&
        sample.TtfbMs is null && sample.TotalMs == 0 &&
        sample.ActualProtocol == "" && sample.ContentType == "";
}

public static class DesktopBenchmarkCopies
{
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ManagerJson.Options), ManagerJson.Options)!;
}
