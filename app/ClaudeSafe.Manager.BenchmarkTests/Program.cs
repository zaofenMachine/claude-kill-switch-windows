using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeSafe.Manager;

Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length > 1 && args[0] == "--live")
{
    string directory = Path.GetFullPath(args[1]);
    var options = new BenchmarkPreferences(); string? node = null, output = null;
    for (int i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--node": node = args[++i]; break;
            case "--rounds": options.Rounds = int.Parse(args[++i]); break;
            case "--protocol": options.Protocol = args[++i]; break;
            case "--timeout": options.TimeoutSeconds = int.Parse(args[++i]); break;
            case "--concurrency": options.Concurrency = int.Parse(args[++i]); break;
            case "--stop-on-first-failure": options.StopOnFirstFailure = true; break;
            case "--output": output = args[++i]; break;
            default: throw new ArgumentException("Unknown live test argument.");
        }
    }
    var settings = JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(Path.Combine(directory, "settings.json")), ManagerJson.Options)!;
    var nodes = SubscriptionSourceReader.LoadActiveNodes(directory);
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    var progress = new InlineProgress(run => Console.WriteLine(JsonSerializer.Serialize(new { @event = "progress", run.Status, run.Completed, run.Total, run.Message })));
    var result = await new DesktopBenchmarkRunner().RunAsync(directory, settings, nodes, options, "manual", node, progress, cancel.Token);
    output ??= Path.Combine(directory, "benchmarks", "live-" + result.Id + ".json");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, ManagerJson.Options), new UTF8Encoding(false));
    Console.WriteLine(JsonSerializer.Serialize(new { @event = "finished", result.Status, result.Completed, result.Total,
        result.Attempted, result.Skipped, AllPassedNodes = result.Ranks.Count(r => r.AllPassed), Report = output, result.Message }));
    Environment.ExitCode = result.Status == "completed" ? 0 : 1; return;
}

int passed = 0;
void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { Console.Error.WriteLine("FAIL " + name + " (" + e.GetType().Name + ")"); Environment.ExitCode = 1; }
}
void Assert(bool value) { if (!value) throw new Exception(); }
void Refuses(Action action) { try { action(); } catch (SafeOperationException) { return; } throw new Exception(); }
var targets = DesktopBenchmarkTargets.Default();
List<BenchmarkSample> Samples(string name, int rounds = 1, string[]? protocols = null, double[]? times = null) => targets.SelectMany((t, index) =>
    (protocols ?? ["http/1.1"]).SelectMany(protocol => Enumerable.Range(1, rounds).Select(round => new BenchmarkSample
    {
        Name = name, TargetId = t.Id, Protocol = protocol, ActualProtocol = protocol, Round = round, Complete = true, TlsVerified = true,
        ResourceValid = t.Kind == "resource", ContentType = t.ExpectedType, Bytes = 100, ExpectedBytes = 100,
        Http = t.Url == "https://api.anthropic.com/" ? 404 : 200, TotalMs = times?[index] ?? 100
    }))).ToList();
BenchmarkRank Rank(string name, List<BenchmarkSample> samples, int rounds = 1, string[]? protocols = null) => DesktopBenchmarkRanker.Rank(name, targets, protocols ?? ["http/1.1"], rounds, samples);
JsonObject Leaf(string name = "Fixture", string server = "example.invalid") => new() { ["name"] = name, ["type"] = "vless", ["server"] = server, ["port"] = 443, ["uuid"] = "fixture-private-value", ["tls"] = true };
ManagerSettings Settings() => new() { InterfaceName = "Fixture Ethernet", Pins = [new("Fixture", "vless", "example.invalid", 443)] };
BenchmarkSample PassingSample(string name, BenchmarkTarget target, string protocol, int round) => new()
{
    Name = name, TargetId = target.Id, Protocol = protocol, ActualProtocol = protocol, Round = round,
    Http = target.Url == "https://api.anthropic.com/" ? 404 : 200, Complete = true, TlsVerified = true,
    ResourceValid = target.Kind == "resource", ContentType = target.ExpectedType,
    Bytes = 100, ExpectedBytes = 100, TtfbMs = 10, TotalMs = 20, Status = "ok"
};

Check("defaults are 24h scheduled with automatic switching disabled", () =>
{ var p = new BenchmarkPreferences(); Assert(p.ScheduleEnabled && p.IntervalHours == 24 && !p.AutoSwitchEnabled); DesktopBenchmarkTargets.ValidatePreferences(p); });
Check("stop on first failure defaults off and persists without changing old preferences", () =>
{
    Assert(!new BenchmarkPreferences().StopOnFirstFailure);
    var old = JsonSerializer.Deserialize<BenchmarkPreferences>("{\"rounds\":2}", ManagerJson.Options)!;
    Assert(!old.StopOnFirstFailure && old.Rounds == 2);
    var enabled = DesktopBenchmarkCopies.Clone(new BenchmarkPreferences { StopOnFirstFailure = true });
    Assert(enabled.StopOnFirstFailure && JsonSerializer.Serialize(old, ManagerJson.Options) != JsonSerializer.Serialize(enabled, ManagerJson.Options));
});
Check("default node execution continues across protocols targets and rounds after a failure", () =>
{
    var options = new BenchmarkPreferences { Rounds = 2, Protocol = "both" }; int calls = 0;
    var data = new List<BenchmarkSample>();
    DesktopBenchmarkRunner.RunNodeSamplesAsync("x", targets, options, (target, protocol, round, _) =>
    {
        var sample = PassingSample("x", target, protocol, round);
        if (++calls == 1) { sample.Complete = false; sample.Status = "timeout"; }
        return Task.FromResult(sample);
    }, data.Add, CancellationToken.None).GetAwaiter().GetResult();
    Assert(calls == targets.Count * 2 * 2 && data.Count == calls && data.All(s => !DesktopBenchmarkSamples.IsSkipped(s)));
    Assert(data[^1].Round == 2 && data[^1].TargetId == targets[^1].Id && data[^1].Protocol == "h2");
});
Check("enabled fail-fast skips every remaining slot for only the failed node", () =>
{
    var options = new BenchmarkPreferences { StopOnFirstFailure = true, Rounds = 3, Protocol = "both" };
    var bad = new List<BenchmarkSample>(); var good = new List<BenchmarkSample>(); int badCalls = 0, goodCalls = 0;
    var badTask = DesktopBenchmarkRunner.RunNodeSamplesAsync("bad", targets, options, (target, protocol, round, _) =>
    {
        var sample = PassingSample("bad", target, protocol, round);
        if (++badCalls == 3) { sample.Bytes = 40; sample.Status = "incomplete"; }
        return Task.FromResult(sample);
    }, bad.Add, CancellationToken.None);
    var goodTask = DesktopBenchmarkRunner.RunNodeSamplesAsync("good", targets, options, (target, protocol, round, _) =>
    { goodCalls++; return Task.FromResult(PassingSample("good", target, protocol, round)); }, good.Add, CancellationToken.None);
    Task.WhenAll(badTask, goodTask).GetAwaiter().GetResult();
    int total = targets.Count * 2 * 3;
    Assert(badCalls == 3 && goodCalls == total && bad.Count == total && good.Count == total);
    Assert(bad.Take(3).All(s => !DesktopBenchmarkSamples.IsSkipped(s)) && bad.Skip(3).All(DesktopBenchmarkSamples.IsCanonicalSkipped));
    Assert(bad[3].Round == 1 && bad[3].TargetId == targets[1].Id && bad[3].Protocol == "h2" && bad[^1].Round == 3);
    var badRank = Rank("bad", bad, 3, ["http/1.1", "h2"]);
    Assert(!badRank.AllPassed && badRank.CoveredSlots == 3 && badRank.SkippedSlots == total - 3 && badRank.PassedSlots == 2);
    Assert(Rank("good", good, 3, ["http/1.1", "h2"]).AllPassed);
    var run = DesktopBenchmarkCopies.Clone(new BenchmarkRun { Samples = bad.Concat(good).ToList() });
    Assert(run.Attempted == total + 3 && run.Skipped == total - 3);
});
Check("a last-slot failure finishes without manufacturing skipped or passing records", () =>
{
    var options = new BenchmarkPreferences { StopOnFirstFailure = true, Rounds = 1, Protocol = "h2" };
    var data = new List<BenchmarkSample>(); int calls = 0;
    DesktopBenchmarkRunner.RunNodeSamplesAsync("x", targets, options, (target, protocol, round, _) =>
    {
        var sample = PassingSample("x", target, protocol, round);
        if (++calls == targets.Count) { sample.Http = 403; sample.Status = "restricted"; }
        return Task.FromResult(sample);
    }, data.Add, CancellationToken.None).GetAwaiter().GetResult();
    Assert(data.Count == targets.Count && data.All(s => !DesktopBenchmarkSamples.IsSkipped(s)) && !Rank("x", data, protocols: ["h2"]).AllPassed);
});
Check("cancellation after failure does not resolve unattempted slots as skipped", () =>
{
    using var cancel = new CancellationTokenSource(); var data = new List<BenchmarkSample>(); int calls = 0;
    try
    {
        DesktopBenchmarkRunner.RunNodeSamplesAsync("x", targets, new() { StopOnFirstFailure = true }, (target, protocol, round, _) =>
        {
            calls++; var sample = PassingSample("x", target, protocol, round); sample.Complete = false;
            return Task.FromResult(sample);
        }, sample => { data.Add(sample); cancel.Cancel(); }, cancel.Token).GetAwaiter().GetResult();
        throw new Exception();
    }
    catch (OperationCanceledException) { }
    Assert(calls == 1 && data.Count == 1 && data.All(s => !DesktopBenchmarkSamples.IsSkipped(s)));
});
Check("an in-flight preference edit cannot disable this node's frozen fail-fast plan", () =>
{
    var options = new BenchmarkPreferences { StopOnFirstFailure = true, Rounds = 1, Protocol = "h2" }; var data = new List<BenchmarkSample>(); int calls = 0;
    DesktopBenchmarkRunner.RunNodeSamplesAsync("x", targets, options, (target, protocol, round, _) =>
    { calls++; var sample = PassingSample("x", target, protocol, round); sample.TlsVerified = false; return Task.FromResult(sample); },
        sample => { data.Add(sample); options.StopOnFirstFailure = false; }, CancellationToken.None).GetAwaiter().GetResult();
    Assert(calls == 1 && data.Skip(1).All(DesktopBenchmarkSamples.IsCanonicalSkipped));
});
Check("skipped samples can never pass even with fabricated response fields", () =>
{
    var sample = PassingSample("x", targets[0], "h2", 1); sample.Status = "skipped";
    Assert(!DesktopBenchmarkRanker.IsPassing(sample, targets[0]) && !DesktopBenchmarkSamples.IsCanonicalSkipped(sample));
    sample.Status = "Skipped"; Assert(!DesktopBenchmarkRanker.IsPassing(sample, targets[0]));
});
Check("options reject invalid schedules protocols rounds concurrency and deadlines", () =>
{
    foreach (double hours in new[] { -1, 0, 0.09, 8761, double.NaN, double.PositiveInfinity }) Refuses(() => DesktopBenchmarkTargets.ValidatePreferences(new() { IntervalHours = hours }));
    foreach (var p in new[] { new BenchmarkPreferences { Rounds = 0 }, new() { Rounds = 11 }, new() { TimeoutSeconds = 2 }, new() { TimeoutSeconds = 61 }, new() { Concurrency = 0 }, new() { Concurrency = 5 }, new() { Protocol = "h3" } }) Refuses(() => DesktopBenchmarkTargets.ValidatePreferences(p));
});
Check("four Desktop targets include three assets and API without a web homepage request", () =>
{ Assert(targets.Count == 4 && targets.Count(t => t.Kind == "resource") == 3 && targets.All(t => new Uri(t.Url).Host != "claude.ai")); foreach (var t in targets) _ = DesktopBenchmarkTargets.Validate(t); });
Check("credential query redirect fragment and unapproved hosts refused", () =>
{
    var credentialUrl = new UriBuilder("https://claude.ai/") { UserName = "fixture", Password = "fixture" }.Uri.AbsoluteUri;
    foreach (var url in new[] { "http://claude.ai/", credentialUrl, "https://claude.ai/?fixture=1", "https://claude.ai/#fragment", "https://example.invalid/", "https://claude.ai/account", "https://claude.ai:444/" })
        Refuses(() => DesktopBenchmarkTargets.Validate(new() { Id = "fixture", Kind = "endpoint", Url = url }));
});
Check("asset targets restricted to approved CDN folder and file kind", () =>
{
    foreach (var url in new[] { "https://claude.ai/claude-ai/v2/assets/v1/x.js", "https://assets-proxy.anthropic.com/other/x.js", "https://assets-proxy.anthropic.com/claude-ai/v2/assets/v1/x.css", "https://assets-proxy.anthropic.com/claude-ai/v2/assets/v1/../x.js" })
        Refuses(() => DesktopBenchmarkTargets.Validate(new() { Id = "fixture", Kind = "resource", ExpectedType = "text/javascript", Url = url }));
});
Check("all targets always outrank faster partial nodes", () =>
{
    var slow = Rank("slow", Samples("slow", times: [900, 900, 900, 900, 900]));
    var fast = Rank("fast", Samples("fast").Take(targets.Count - 1).ToList()); Assert(slow.AllPassed && !fast.AllPassed && DesktopBenchmarkRanker.Compare(slow, fast) < 0);
});
Check("truncated HTTP200 and byte mismatch fail", () =>
{
    var data = Samples("x"); data[0].Complete = false; Assert(!Rank("x", data).AllPassed);
    data[0].Complete = true; data[0].Bytes = 99; Assert(!Rank("x", data).AllPassed);
});
Check("MIME verification ignores resourceValid claims and accepts standard JS aliases", () =>
{
    var data = Samples("x"); data[0].ContentType = "text/html"; Assert(!Rank("x", data).AllPassed);
    data[0].ContentType = "application/javascript; charset=utf-8"; Assert(Rank("x", data).AllPassed);
    data[2].ContentType = "text/javascript"; Assert(!Rank("x", data).AllPassed);
});
Check("empty resources and missing TLS verification fail", () =>
{
    var data = Samples("x"); data[0].Bytes = 0; data[0].ExpectedBytes = 0; Assert(!Rank("x", data).AllPassed);
    data = Samples("x"); data[0].TlsVerified = false; Assert(!Rank("x", data).AllPassed);
});
Check("only API root404 is successful reachability", () =>
{
    var data = Samples("x"); Assert(Rank("x", data).AllPassed);
    data[0].Http = 404; Assert(!Rank("x", data).AllPassed);
    foreach (var code in new[] { 401, 403, 429, 500, 502, 503 }) { data = Samples("x"); data[^1].Http = code; Assert(!Rank("x", data).AllPassed); }
});
Check("endpoint302 is anonymous reachability but resource302 fails", () =>
{
    var data = Samples("x"); data[3].Http = 302; Assert(Rank("x", data).AllPassed);
    Assert(DesktopBenchmarkHttpProbe.Classify(data[3], targets[3]) == "redirect");
    data[0].Http = 302; Assert(!Rank("x", data).AllPassed);
});
Check("resource404 and410 classified as stale", () =>
{
    foreach (var code in new[] { 404, 410 }) { var data = Samples("x"); data[0].Http = code; Assert(DesktopBenchmarkHttpProbe.Classify(data[0], targets[0]) == "target_stale"); }
});
Check("all requested rounds required and duplicates do not fill missing rounds", () =>
{
    var data = Samples("x"); Assert(!Rank("x", data, 3).AllPassed);
    Assert(!Rank("x", data.Concat(data).Concat(data).ToList(), 3).AllPassed);
    var all = Samples("x", 3); Assert(Rank("x", all, 3).AllPassed); all[1].Complete = false; Assert(!Rank("x", all, 3).AllPassed);
});
Check("both protocols and actual negotiation must match", () =>
{
    string[] protocols = ["http/1.1", "h2"]; var data = Samples("x"); Assert(!Rank("x", data, protocols: protocols).AllPassed);
    data = Samples("x", protocols: protocols); Assert(Rank("x", data, protocols: protocols).AllPassed);
    data[1].ActualProtocol = "http/1.1"; Assert(!Rank("x", data, protocols: protocols).AllPassed);
});
Check("slowest target median precedes overall average", () =>
{ Assert(DesktopBenchmarkRanker.Compare(Rank("balanced", Samples("balanced", times: [300, 300, 300, 300])), Rank("spike", Samples("spike", times: [50, 50, 50, 400]))) < 0); });
Check("average and slowest single sample break median ties", () =>
{
    Assert(DesktopBenchmarkRanker.Compare(Rank("a", Samples("a", times: [100, 100, 100, 400])), Rank("b", Samples("b", times: [300, 300, 300, 400]))) < 0);
    var a = Samples("a", 3); var b = Samples("b", 3); b[0].TotalMs = 900;
    Assert(Rank("a", a, 3).WorstMedianMs == Rank("b", b, 3).WorstMedianMs && DesktopBenchmarkRanker.Compare(Rank("a", a, 3), Rank("b", b, 3)) < 0);
});
Check("natural names break final ties", () => Assert(DesktopBenchmarkRanker.Compare(Rank("节点2", []), Rank("节点10", [])) < 0));
Check("empty targets protocols and untested nodes never pass", () =>
{
    Assert(!Rank("x", []).AllPassed && !DesktopBenchmarkRanker.Rank("x", [], ["http/1.1"], 1, []).AllPassed &&
        !DesktopBenchmarkRanker.Rank("x", targets, [], 1, []).AllPassed);
});
Check("invalid numeric delays fail and no Infinity is serialized for incomplete ranks", () =>
{
    foreach (var delay in new[] { -1, double.NaN, double.PositiveInfinity }) { var data = Samples("x"); data[0].TotalMs = delay; Assert(!Rank("x", data).AllPassed); }
    var json = JsonSerializer.Serialize(Rank("x", []), ManagerJson.Options); Assert(!json.Contains("Infinity") && json.Contains("null"));
});
Check("independent config contains only approved loopback leaves and reject fallback", () =>
{
    var config = DesktopBenchmarkRunner.BuildIsolatedConfiguration(Settings(), [Leaf()], new Dictionary<string, int> { ["Fixture"] = 51001 });
    Assert(config["external-controller"]!.GetValue<string>() == "" && config["secret"] is null && config["proxy-groups"]!.AsArray().Count == 0 &&
        !config["tun"]!["enable"]!.GetValue<bool>() && config["rules"]![0]!.GetValue<string>() == "MATCH,REJECT");
    Assert(config["listeners"]![0]!["listen"]!.GetValue<string>() == "127.0.0.1" && config["listeners"]![0]!["proxy"]!.GetValue<string>() == "Fixture" && config["listeners"]![0]!["port"]!.GetValue<int>() == 51001);
    Assert(config["interface-name"]!.GetValue<string>() == "Fixture Ethernet" && config["dns"]!["nameserver"]![0]!.GetValue<string>().EndsWith("#Fixture Ethernet"));
});
Check("reserved duplicate and invalid ports cannot become test listeners", () =>
{
    foreach (var port in new[] { 0, 65536, 7897, 17987, 17990 }) Refuses(() => DesktopBenchmarkRunner.BuildIsolatedConfiguration(Settings(), [Leaf()], new Dictionary<string, int> { ["Fixture"] = port }));
    Refuses(() => DesktopBenchmarkRunner.BuildIsolatedConfiguration(Settings(), [Leaf()], new Dictionary<string, int> { ["Fixture"] = 51001, ["Other"] = 51001 }));
});
Check("unapproved drift and unsafe leaves never enter test listeners", () =>
{
    var ports = new Dictionary<string, int> { ["Fixture"] = 51001 };
    Refuses(() => DesktopBenchmarkRunner.BuildIsolatedConfiguration(Settings(), [Leaf(server: "changed.invalid")], ports));
    var settings = Settings(); settings.Pins.Clear(); Refuses(() => DesktopBenchmarkRunner.BuildIsolatedConfiguration(settings, [Leaf()], ports));
    var node = Leaf(); node["dialer-proxy"] = "DIRECT"; Refuses(() => DesktopBenchmarkRunner.BuildIsolatedConfiguration(Settings(), [node], ports));
});
Check("handler disables cookies redirects decompression and direct fallback", () =>
{
    using var handler = DesktopBenchmarkHttpProbe.CreateHandler(51001);
    Assert(handler.UseProxy && !handler.UseCookies && !handler.AllowAutoRedirect && !handler.PreAuthenticate && handler.Credentials is null && handler.ConnectCallback is not null);
    Assert(handler.AutomaticDecompression == DecompressionMethods.None && handler.Proxy!.GetProxy(new Uri("https://claude.ai/"))!.ToString() == "http://127.0.0.1:51001/");
});
Check("pre-cancelled probe performs no request", () =>
{
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    var result = DesktopBenchmarkHttpProbe.ProbeAsync("Fixture", 51001, targets[0], "h2", 1, 3, cancel.Token).GetAwaiter().GetResult();
    Assert(result.Status == "cancelled" && !result.Complete && result.Bytes == 0 && result.Http == 0);
});
Check("cancelled runner preserves failure and returns detached progress copies", () =>
{
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    var result = new DesktopBenchmarkRunner().RunAsync(Path.GetTempPath(), Settings(), [Leaf()], new(), "manual", null,
        new InlineProgress(copy => copy.NodeNames.Add("observer-only")), cancel.Token).GetAwaiter().GetResult();
    Assert(result.Cancelled && result.Status == "cancelled" && result.NodeNames.Count == 0 && result.Samples.Count == 0);
});
Check("target manifest may refresh assets but cannot drop or replace required endpoints", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "claudesafe-benchmark-targets-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    string file = Path.Combine(directory, "desktop-targets.json");
    try
    {
        var definitions = DesktopBenchmarkTargets.Default(); definitions[0].Url = "https://assets-proxy.anthropic.com/claude-ai/v2/assets/v1/refreshed.js";
        File.WriteAllText(file, JsonSerializer.Serialize(definitions, ManagerJson.Options)); Assert(DesktopBenchmarkTargets.Load(directory)[0].Url.EndsWith("/refreshed.js"));
        definitions.RemoveAt(0); File.WriteAllText(file, JsonSerializer.Serialize(definitions, ManagerJson.Options)); Refuses(() => DesktopBenchmarkTargets.Load(directory));
        definitions = DesktopBenchmarkTargets.Default(); definitions[3].Url = "https://example.invalid/"; File.WriteAllText(file, JsonSerializer.Serialize(definitions, ManagerJson.Options)); Refuses(() => DesktopBenchmarkTargets.Load(directory));
    }
    finally { if (File.Exists(file)) File.Delete(file); Directory.Delete(directory); }
});
void WithManifest(List<BenchmarkTarget> definitions, Action<string> test)
{
    string directory = Path.Combine(Path.GetTempPath(), "claudesafe-desktop-plan-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory); string file = Path.Combine(directory, "desktop-targets.json");
    try { File.WriteAllText(file, JsonSerializer.Serialize(definitions, ManagerJson.Options)); test(directory); }
    finally { if (File.Exists(file)) File.Delete(file); Directory.Delete(directory); }
}
BenchmarkTarget LegacyHomepage() => new() { Id = "claude.ai", Label = "Old web homepage", Kind = "endpoint", Url = "https://claude.ai/" };
Check("legacy five-target overrides retire only the known web homepage", () =>
{
    var definitions = DesktopBenchmarkTargets.Default(); definitions[0].Url = "https://assets-proxy.anthropic.com/claude-ai/v2/assets/v1/updated.js";
    definitions.Add(LegacyHomepage());
    WithManifest(definitions, directory =>
    { var loaded = DesktopBenchmarkTargets.Load(directory); Assert(loaded.Count == 4 && loaded.All(t => !DesktopBenchmarkTargets.IsLegacyHomepage(t)) && loaded[0].Url.EndsWith("/updated.js")); });
});
Check("legacy migration cannot replace a required resource with the homepage", () =>
{
    var definitions = DesktopBenchmarkTargets.Default(); definitions.RemoveAt(0); definitions.Add(LegacyHomepage());
    WithManifest(definitions, directory => Refuses(() => DesktopBenchmarkTargets.Load(directory)));
});
Check("unknown extras or duplicate legacy homepage entries remain refused", () =>
{
    var definitions = DesktopBenchmarkTargets.Default(); var unknown = LegacyHomepage(); unknown.Id = "unknown-home"; definitions.Add(unknown);
    WithManifest(definitions, directory => Refuses(() => DesktopBenchmarkTargets.Load(directory)));
    definitions = DesktopBenchmarkTargets.Default(); definitions.Add(LegacyHomepage()); definitions.Add(LegacyHomepage());
    WithManifest(definitions, directory => Refuses(() => DesktopBenchmarkTargets.Load(directory)));
});
Check("resource and API 403 still fail the Desktop-only plan", () =>
{
    var data = Samples("x"); data[0].Http = 403; Assert(!Rank("x", data).AllPassed);
    data = Samples("x"); data[^1].Http = 403; Assert(!Rank("x", data).AllPassed);
});
Console.WriteLine($"{passed} desktop benchmark checks passed.");

sealed class InlineProgress(Action<BenchmarkRun> report) : IProgress<BenchmarkRun>
{ public void Report(BenchmarkRun value) => report(value); }
