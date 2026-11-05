using System.Text.Json;

namespace ClaudeSafe.Manager;

public sealed class ManagerSettings
{
    public string CorePath { get; set; } = "";
    public string CoreSha256 { get; set; } = "";
    public string InterfaceName { get; set; } = "";
    public string ExpectedCountry { get; set; } = "SG";
    public List<NodePin> Pins { get; set; } = [];
    public BenchmarkPreferences Benchmark { get; set; } = new();
    public DateTimeOffset? NextBenchmarkUtc { get; set; }
}

public sealed record NodePin(string Name, string Type, string Server, int Port);

public sealed class NodeSummary
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public bool Approved { get; init; }
    public bool Eligible { get; init; }
    public bool CanApprove { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class ManagerSnapshot
{
    public bool Running { get; init; }
    public int? ProcessId { get; init; }
    public string Selected { get; init; } = "REJECT";
    public string InterfaceName { get; init; } = "";
    public string Message { get; init; } = "";
    public List<NodeSummary> Nodes { get; init; } = [];
    public bool AutoSwitchEnabled { get; init; }
}

public sealed class ProbeResult
{
    public bool Success { get; init; }
    public string Country { get; init; } = "";
    public string Message { get; init; } = "";
    public long ElapsedMs { get; init; }
}

public sealed class SafeOperationException(string message) : Exception(message);

public static class ManagerJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 32
    };
}
