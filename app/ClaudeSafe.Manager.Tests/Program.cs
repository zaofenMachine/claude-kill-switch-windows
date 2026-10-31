using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Diagnostics;
using ClaudeSafe.Manager;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

if (args.Length > 1 && args[0] == "--sync-dispose")
{
    try
    {
        var result = SyncDisposeCheck.Run(args[1], startCore: true);
        Console.WriteLine(JsonSerializer.Serialize(result)); Environment.ExitCode = result.Passed ? 0 : 1;
    }
    catch (SafeOperationException e) { Console.WriteLine(JsonSerializer.Serialize(new { @event = "error", message = e.Message })); Environment.ExitCode = 1; }
    return;
}

if (args.Length > 0 && args[0] == "--encoding-check")
{
    const string fixture = "🇸🇬 网卡与节点 𠮷";
    bool matches = args.Length > 1 && args[1] == fixture && Console.ReadLine() == fixture;
    Console.WriteLine(JsonSerializer.Serialize(new { encodingRoundTrip = matches }));
    Environment.ExitCode = matches ? 0 : 1;
    return;
}

if (args.Length > 1 && args[0] is "--hold" or "--live")
{
    using var manager = new ManagerService(args[1]);
    void Report(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
    try
    {
        await manager.StartAsync(); Report(new { @event = "started", state = await manager.SnapshotAsync() });
        string name = args.Length > 2 ? args[2] : "REJECT";
        if (args[0] == "--live" && name == "REJECT")
            name = (await manager.SnapshotAsync()).Nodes.FirstOrDefault(n => n.Approved && n.Eligible)?.Name ?? "REJECT";
        if (name != "REJECT") { await manager.SelectAsync(name); Report(new { @event = "selected", state = await manager.SnapshotAsync() }); }
        if (args[0] == "--live") { Report(new { @event = "probe", result = await manager.ProbeAsync() }); await manager.StopAsync(); return; }
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            try
            {
                if (line == "stop") { await manager.StopAsync(); Report(new { @event = "stopped" }); break; }
                if (line == "probe") Report(new { @event = "probe", result = await manager.ProbeAsync() });
                else if (line == "block") { await manager.SelectAsync("REJECT"); Report(new { @event = "blocked", state = await manager.SnapshotAsync() }); }
                else if (line.StartsWith("select ", StringComparison.Ordinal)) { await manager.SelectAsync(line[7..]); Report(new { @event = "selected", state = await manager.SnapshotAsync() }); }
                else if (line == "status") Report(new { @event = "status", state = await manager.SnapshotAsync() });
                else Report(new { @event = "error", message = "Unknown test command." });
            }
            catch (SafeOperationException e) { Report(new { @event = "error", message = e.Message }); }
        }
    }
    catch (SafeOperationException e) { Report(new { @event = "error", message = e.Message }); Environment.ExitCode = 1; }
    return;
}

int passed = 0;
void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch { Console.Error.WriteLine("FAIL " + name); Environment.ExitCode = 1; }
}
void Assert(bool condition) { if (!condition) throw new Exception(); }
void Refuses(Action action) { try { action(); } catch (SafeOperationException) { return; } throw new Exception(); }
JsonObject Leaf(string name = "Fixture", string server = "example.invalid") => new()
{ ["name"] = name, ["type"] = "vless", ["server"] = server, ["port"] = 443, ["uuid"] = "fixture-private-value", ["tls"] = true };
ManagerSettings Settings() => new() { InterfaceName = "Fixture Ethernet", Pins = [new NodePin("Fixture", "vless", "example.invalid", 443)] };
JsonObject Build(ManagerSettings settings, params JsonObject[] nodes) => CoreConfig.Build(settings, nodes, "fixture-token");

Check("empty approvals produce REJECT only", () =>
{
    var settings = Settings(); settings.Pins.Clear(); var config = Build(settings, Leaf());
    Assert(config["proxies"]!.AsArray().Count == 0 && config["proxy-groups"]![0]!["proxies"]!.AsArray().Count == 1);
});
Check("approved identity exact match only", () =>
{
    var config = Build(Settings(), Leaf(), Leaf("Unapproved")); Assert(config["proxies"]!.AsArray().Count == 1);
});
Check("identity drift omitted", () => Assert(Build(Settings(), Leaf(server: "changed.invalid"))["proxies"]!.AsArray().Count == 0));
Check("identity drift requires reapproval but safe leaf can be approved", () =>
{
    var summary = CoreConfig.Summaries(Settings(), [Leaf(server: "changed.invalid")]).Single();
    Assert(!summary.Approved && !summary.Eligible && summary.CanApprove);
    var unsafeNode = Leaf(); unsafeNode["dialer-proxy"] = "Other";
    Assert(!CoreConfig.Summaries(Settings(), [unsafeNode]).Single().CanApprove);
});
Check("startup group is REJECT and cache disabled", () =>
{
    var config = Build(Settings(), Leaf()); Assert(config["proxy-groups"]![0]!["proxies"]![0]!.GetValue<string>() == "REJECT");
    Assert(!config["profile"]!["store-selected"]!.GetValue<bool>() && config["rules"]![0]!.GetValue<string>() == "MATCH,REJECT");
});
Check("TUN disabled and only loopback dedicated listener", () =>
{
    var config = Build(Settings(), Leaf()); Assert(!config["tun"]!["enable"]!.GetValue<bool>() && config["mixed-port"]!.GetValue<int>() == 0);
    Assert(config["listeners"]![0]!["listen"]!.GetValue<string>() == "127.0.0.1" && !config["listeners"]![0]!["udp"]!.GetValue<bool>());
});
Check("explicit physical interface on DNS and outbounds", () =>
{
    var config = Build(Settings(), Leaf()); Assert(config["interface-name"]!.GetValue<string>() == "Fixture Ethernet");
    Assert(config["dns"]!["proxy-server-nameserver"]![0]!.GetValue<string>().EndsWith("#Fixture Ethernet") && config["dns"]!["listen"] is null);
});
Check("proxy chains and interface overrides omitted", () =>
{
    foreach (var key in new[] { "dialer-proxy", "interface-name", "routing-mark" })
    { var node = Leaf(); node[key] = "other"; Assert(Build(Settings(), node)["proxies"]!.AsArray().Count == 0); }
});
Check("skip certificate verification omitted", () =>
{ var node = Leaf(); node["skip-cert-verify"] = true; Assert(Build(Settings(), node)["proxies"]!.AsArray().Count == 0); });
Check("credential file options omitted", () =>
{ var node = Leaf(); node["reality-opts"] = new JsonObject { ["certificate"] = "fixture.pem" }; Assert(Build(Settings(), node)["proxies"]!.AsArray().Count == 0); });
Check("node name cannot shadow DNS interface", () =>
{
    var settings = Settings(); settings.InterfaceName = "Fixture"; Assert(Build(settings, Leaf())["proxies"]!.AsArray().Count == 0);
});
Check("local proxy endpoints omitted", () =>
{
    foreach (var host in new[] { "localhost", "127.0.0.1", "::1", "0.0.0.0" }) Assert(CoreConfig.IneligibleReason(Leaf(server: host)).Length > 0);
});
Check("unknown options do not silently pass", () =>
{ var node = Leaf(); node["future-routing-option"] = "unsafe"; Assert(Build(Settings(), node)["proxies"]!.AsArray().Count == 0); });
Check("duplicate approvals refused", () =>
{ var settings = Settings(); settings.Pins.Add(settings.Pins[0]); Refuses(() => Build(settings, Leaf())); });
Check("JSON duplicate fields and node names refused", () =>
{
    Refuses(() => CoreConfig.ParseSource("{\"proxies\":[],\"proxies\":[]}"));
    Refuses(() => CoreConfig.ParseSource(new JsonObject { ["proxies"] = new JsonArray(Leaf(), Leaf()) }.ToJsonString()));
});
Check("YAML source import preserves only leaves", () =>
{
    var nodes = CoreConfig.ParseSource("mixed-port: 9\ntun: {enable: true}\nproxies:\n- name: Fixture\n  type: vless\n  server: example.invalid\n  port: 443\n  uuid: '0000123'\n");
    Assert(nodes.Count == 1 && nodes[0]["uuid"]!.GetValue<string>() == "0000123" && !Build(Settings(), nodes[0])["tun"]!["enable"]!.GetValue<bool>());
});
Check("YAML aliases custom tags multiple documents refused", () =>
{
    Refuses(() => CoreConfig.ParseSource("proxies: &a []\nother: *a"));
    Refuses(() => CoreConfig.ParseSource("proxies: !unsafe []"));
    Refuses(() => CoreConfig.ParseSource("!unsafe proxies: []"));
    Refuses(() => CoreConfig.ParseSource("proxies: []\n---\nproxies: []"));
});
Check("unapproved but safe node can be reviewed", () =>
{
    var settings = Settings(); settings.Pins.Clear(); var summary = CoreConfig.Summaries(settings, [Leaf()]).Single();
    Assert(!summary.Approved && summary.Eligible);
});
Check("source missing proxies refused", () => Refuses(() => CoreConfig.ParseSource("{\"proxy-providers\":{}}")));
Check("oversized source refused", () => Refuses(() => CoreConfig.ParseSource(new string('x', CoreConfig.MaximumSourceBytes + 1))));
Check("invalid interface syntax refused", () =>
{ var settings = Settings(); settings.InterfaceName = "Ethernet#DIRECT"; Refuses(() => Build(settings, Leaf())); });
Check("YAML core serialization preserves supplementary names and literal escapes", () =>
{
    var node = Leaf("🇸🇬 Fixture 𠮷"); node["password"] = @"literal\uD83C\uDDF8\n\u0022";
    var settings = Settings(); settings.Pins = [CoreConfig.Identity(node)];
    var yaml = CoreConfig.SerializeForCore(Build(settings, node));
    Assert(!yaml.Contains(@"\uD83C", StringComparison.OrdinalIgnoreCase) || yaml.Contains(@"literal\uD83C", StringComparison.Ordinal));
    var imported = CoreConfig.ParseSource(yaml).Single();
    Assert(CoreConfig.Identity(imported).Name == "🇸🇬 Fixture 𠮷" && imported["password"]!.GetValue<string>() == @"literal\uD83C\uDDF8\n\u0022");
});
Check("backend import refresh and dispose do not need a UI message pump", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "claudesafe-sync-fixture-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var fixture = Path.Combine(directory, "fixture.json");
    File.WriteAllText(fixture, "{\"proxies\":[],\"ignored\":\"" + new string('x', 2 * 1024 * 1024) + "\"}");
    try { Assert(SyncDisposeCheck.Run(directory, startCore: false, fixture).Passed); }
    finally
    {
        // Delete only the three explicitly owned files, then the now-empty fixture directory.
        foreach (var name in new[] { "fixture.json", "source.json", "settings.json" })
        { var file = Path.Combine(directory, name); if (File.Exists(file)) File.Delete(file); }
        Directory.Delete(directory);
    }
});
Console.WriteLine($"{passed} offline manager checks passed.");

internal sealed class NoPumpSynchronizationContext : SynchronizationContext
{
    public int Posts;
    public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref Posts);
}

internal sealed record SyncDisposeResult(bool Passed, bool CoreStarted, bool ChildExited, bool SnapshotCompleted, int PostedContinuations, long ElapsedMs);

internal static class SyncDisposeCheck
{
    public static SyncDisposeResult Run(string directory, bool startCore, string? importFixture = null)
    {
        var manager = new ManagerService(directory);
        int? childId = null;
        try
        {
            if (startCore)
            {
                Task.Run(manager.StartAsync).GetAwaiter().GetResult();
                var before = Task.Run(manager.SnapshotAsync).GetAwaiter().GetResult();
                if (!before.Running || before.Selected != "REJECT") throw new SafeOperationException("同步退出检查没有获得 REJECT 状态的专用内核。");
                childId = before.ProcessId;
            }
            var context = new NoPumpSynchronizationContext(); var finished = new ManualResetEventSlim();
            Exception? failure = null; bool snapshotCompleted = false;
            var timer = Stopwatch.StartNew();
            var ui = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(context);
                try
                {
                    if (importFixture is not null) manager.ImportFileAsync(importFixture).GetAwaiter().GetResult();
                    // With a live core, this asynchronous controller read owns the gate when Dispose begins.
                    var refresh = manager.SnapshotAsync();
                    manager.Dispose();
                    _ = refresh.GetAwaiter().GetResult(); snapshotCompleted = refresh.IsCompletedSuccessfully;
                }
                catch (Exception e) { failure = e; }
                finally { SynchronizationContext.SetSynchronizationContext(null); finished.Set(); }
            }) { IsBackground = true };
            ui.Start();
            if (!finished.Wait(TimeSpan.FromSeconds(10)))
                return new SyncDisposeResult(false, startCore, false, false, context.Posts, timer.ElapsedMilliseconds);
            bool childExited = true;
            if (childId is int id)
            {
                try { using var child = Process.GetProcessById(id); childExited = child.HasExited; }
                catch (ArgumentException) { childExited = true; }
            }
            return new SyncDisposeResult(failure is null && childExited && snapshotCompleted && context.Posts == 0,
                startCore, childExited, snapshotCompleted, context.Posts, timer.ElapsedMilliseconds);
        }
        catch { manager.Dispose(); throw; }
    }
}
