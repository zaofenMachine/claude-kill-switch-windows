using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Reflection;
using ClaudeSafe.Manager;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

if (args.Length > 1 && args[0] == "--dispose-live")
{
    var manager = new ManagerService(args[1]);
    await manager.StartBenchmarkAsync();
    var timer = Stopwatch.StartNew();
    while ((await manager.BenchmarkSnapshotAsync()).Run?.Status != "running")
    {
        if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new Exception("Benchmark did not become active.");
        await Task.Delay(50);
    }
    manager.Dispose();
    var run = JsonSerializer.Deserialize<BenchmarkRun>(File.ReadAllText(Path.Combine(args[1], "benchmarks", "latest.json")), ManagerJson.Options)!;
    if (!run.Cancelled || run.Status != "cancelled" || run.FinishedUtc is null) throw new Exception("Disposal failed to cancel benchmark.");
    string corePath = Path.Combine(Path.GetFullPath(args[1]), "bin", "claude-safe-mihomo.exe");
    foreach (var process in Process.GetProcessesByName("claude-safe-mihomo"))
    {
        using (process)
            if (string.Equals(process.MainModule?.FileName, corePath, StringComparison.OrdinalIgnoreCase)) throw new Exception("A test core survived disposal.");
    }
    Console.WriteLine(JsonSerializer.Serialize(new { disposeCancelledBenchmark = true, ownTestCoreExited = true, run.Completed, run.Total }));
    return;
}

if (args.Length > 1 && args[0] == "--control-live")
{
    // Synthetic result matrices exercise real selector mutations, not network quality.
    // Run only with an isolated private validation profile and no protected clients open.
    using var manager = new ManagerService(args[1]);
    var stored = JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(Path.Combine(args[1], "settings.json")), ManagerJson.Options)!;
    if (stored.Pins.Count != 2) throw new Exception("Exactly two fixture-approved nodes are required.");
    string a = stored.Pins[0].Name, b = stored.Pins[1].Name;
    var preferences = new BenchmarkPreferences { ScheduleEnabled = false, AutoSwitchEnabled = true, Rounds = 1, Protocol = "http/1.1" };
    await manager.SaveBenchmarkPreferencesAsync(preferences);
    await manager.StartAsync();
    int checks = 0;
    var serviceType = typeof(ManagerService); const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    var complete = serviceType.GetMethod("CompleteBenchmarkAsync", hidden)!;
    object[] Capture(string initial, CancellationToken cancellation) =>
    [
        initial, serviceType.GetField("configurationRevision", hidden)!.GetValue(manager)!,
        serviceType.GetField("manualRevision", hidden)!.GetValue(manager)!,
        serviceType.GetMethod("BenchmarkInputFingerprint", hidden)!.Invoke(manager, null)!,
        JsonSerializer.Serialize(DesktopBenchmarkTargets.Load(args[1]), ManagerJson.Options), cancellation
    ];
    BenchmarkRun Matrix(bool aPass, bool bPass)
    {
        var targets = DesktopBenchmarkTargets.Default();
        var samples = new[] { (a, aPass), (b, bPass) }.SelectMany(pair => targets.Select(t => new BenchmarkSample
        {
            Name = pair.Item1, TargetId = t.Id, Protocol = "http/1.1", ActualProtocol = "http/1.1", Round = 1,
            Complete = pair.Item2, TlsVerified = true, Http = t.Url == "https://api.anthropic.com/" ? 404 : 200,
            ResourceValid = true, ContentType = t.ExpectedType, Bytes = 100, ExpectedBytes = 100, TotalMs = pair.Item1 == a ? 200 : 100
        })).ToList();
        var run = new BenchmarkRun { Id = "synthetic-" + Guid.NewGuid().ToString("N"), Trigger = "scheduled", Status = "completed",
            StartedUtc = DateTimeOffset.UtcNow, FinishedUtc = DateTimeOffset.UtcNow, Options = preferences,
            Targets = targets, NodeNames = [a, b], Samples = samples, Total = samples.Count, Completed = samples.Count,
            Message = "合成测速结果，仅验证真实内核控制流程，不代表真实网络全通过。" };
        run.Ranks = run.NodeNames.Select(n => DesktopBenchmarkRanker.Rank(n, targets, ["http/1.1"], 1, samples)).ToList();
        return run;
    }
    async Task Apply(BenchmarkRun run, object[] capture, string expected, string name)
    {
        await (Task)complete.Invoke(manager, new object[] { run }.Concat(capture).ToArray())!;
        if ((await manager.SnapshotAsync()).Selected != expected) throw new Exception("Control integration failed: " + name);
        checks++; Console.WriteLine("PASS " + name);
    }
    await manager.SelectAsync(a);
    await Apply(Matrix(true, true), Capture(a, CancellationToken.None), a, "healthy current node stays despite faster peer");
    await Apply(Matrix(false, true), Capture(a, CancellationToken.None), b, "failed current switches to fully passing best node");
    await manager.SelectAsync(a);
    await Apply(Matrix(false, false), Capture(a, CancellationToken.None), "REJECT", "no fully passing candidates switches to REJECT");
    await Apply(Matrix(false, true), Capture("REJECT", CancellationToken.None), "REJECT", "manual blocking is never automatically cleared");
    await manager.SelectAsync(a); var saved = Capture(a, CancellationToken.None); await manager.SelectAsync(a);
    await Apply(Matrix(false, true), saved, a, "manual reselection during a run vetoes switching");
    saved = Capture(a, CancellationToken.None); preferences.Concurrency = 1; await manager.SaveBenchmarkPreferencesAsync(preferences);
    await Apply(Matrix(false, true), saved, a, "configuration changes veto switching");
    using var cancellation = new CancellationTokenSource(); saved = Capture(a, cancellation.Token);
    var completionGate = (SemaphoreSlim)serviceType.GetField("gate", hidden)!.GetValue(manager)!;
    await completionGate.WaitAsync(); Task pending;
    try { pending = (Task)complete.Invoke(manager, new object[] { Matrix(false, true) }.Concat(saved).ToArray())!; cancellation.Cancel(); }
    finally { completionGate.Release(); }
    await pending;
    if ((await manager.SnapshotAsync()).Selected != a || (await manager.BenchmarkSnapshotAsync()).Run?.Cancelled != true)
        throw new Exception("Late cancellation was not a veto.");
    checks++; Console.WriteLine("PASS cancelled completion waiting on gate vetoes switching");
    var changedTargets = Matrix(false, true); changedTargets.Targets[0].Url = "https://assets-proxy.anthropic.com/claude-ai/v2/assets/v1/fixture-changed.js";
    await Apply(changedTargets, Capture(a, CancellationToken.None), a, "actual run targets must match the original target fingerprint");
    var subset = Matrix(false, true); subset.NodeNames = [a]; subset.Samples = subset.Samples.Where(s => s.Name == a).ToList();
    subset.Ranks = subset.Ranks.Where(r => r.Name == a).ToList(); subset.Total = subset.Completed = subset.Samples.Count;
    await Apply(subset, Capture(a, CancellationToken.None), a, "a self-consistent subset cannot count as the full approved schedule");
    var changedOptions = Matrix(false, true); changedOptions.Options = DesktopBenchmarkCopies.Clone(preferences); changedOptions.Options.TimeoutSeconds = 21;
    await Apply(changedOptions, Capture(a, CancellationToken.None), a, "actual run options must match the frozen plan");
    preferences.AutoSwitchEnabled = false; await manager.SaveBenchmarkPreferencesAsync(preferences);
    await Apply(Matrix(false, false), Capture(a, CancellationToken.None), a, "disabled automation never changes current selection");
    await manager.StopAsync(); Console.WriteLine(JsonSerializer.Serialize(new { realCoreControlChecks = checks, syntheticNetworkResults = true, accountRequestsSent = false }));
    return;
}

if (args.Length > 1 && args[0] is "--live" or "--scheduled-live" or "--cancel-live")
{
    // These modes are only for an explicitly prepared private validation directory.
    using var manager = new ManagerService(args[1]);
    var initial = await manager.BenchmarkSnapshotAsync();
    if (args[0] != "--scheduled-live") await manager.StartBenchmarkAsync();
    var deadline = Stopwatch.StartNew(); bool cancelled = false; int previous = -1; BenchmarkManagerSnapshot state;
    do
    {
        await Task.Delay(400); state = await manager.BenchmarkSnapshotAsync();
        if ((state.Run?.Completed ?? -1) != previous)
        {
            previous = state.Run?.Completed ?? -1;
            Console.WriteLine(JsonSerializer.Serialize(new { status = state.Run?.Status, completed = state.Run?.Completed, total = state.Run?.Total }));
        }
        if (args[0] == "--cancel-live" && state.Busy && !cancelled && state.Run?.Status == "running")
        { await manager.CancelBenchmarkAsync(); cancelled = true; }
        if (deadline.Elapsed > TimeSpan.FromMinutes(20)) throw new Exception("Validation deadline exceeded.");
    } while (state.Busy || state.Run?.FinishedUtc is null);
    bool scheduled = args[0] == "--scheduled-live";
    if (scheduled && (state.Run.Trigger != "scheduled" || state.NextDueUtc <= DateTimeOffset.UtcNow)) throw new Exception("Schedule did not fire and reschedule.");
    if (args[0] == "--cancel-live" && (!state.Run.Cancelled || state.Run.Status != "cancelled")) throw new Exception("Cancellation failed.");
    if (args[0] != "--cancel-live" && state.Run.Status != "completed") throw new Exception("Benchmark did not complete. Inspect local latest.json.");
    if ((await manager.SnapshotAsync()).Running) throw new Exception("Benchmark unexpectedly started the production proxy.");
    Console.WriteLine(JsonSerializer.Serialize(new { finished = true, state.Run.Status, scheduled, state.NextDueUtc,
        state.Run.Completed, state.Run.Total, state.Run.AutoSwitchOutcome, nodes = state.Run.NodeNames.Count,
        fullyPassingNodes = state.Run.Ranks.Count(r => r.AllPassed), productionCoreStarted = false }));
    return;
}

int passed = 0;
void Check(string name, Action check)
{
    try { check(); passed++; Console.WriteLine("PASS " + name); }
    catch { Console.WriteLine("FAIL " + name); Environment.ExitCode = 1; }
}
void Assert(bool condition) { if (!condition) throw new Exception(); }
var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
Check("defaults schedule 24h with automatic switch disabled", () =>
{
    var preferences = new BenchmarkPreferences();
    Assert(preferences.ScheduleEnabled && preferences.IntervalHours == 24 && !preferences.AutoSwitchEnabled);
    Assert(BenchmarkSchedule.NextDue(now, preferences) == now.AddHours(24));
});
Check("disabled schedules have no next due", () => Assert(BenchmarkSchedule.NextDue(now, new BenchmarkPreferences { ScheduleEnabled = false }) is null));
Check("due boundary and active-run overlap are respected", () =>
{
    Assert(!BenchmarkSchedule.IsDue(now, now.AddSeconds(1), true, false));
    Assert(BenchmarkSchedule.IsDue(now, now, true, false));
    Assert(!BenchmarkSchedule.IsDue(now, now, true, true));
    Assert(!BenchmarkSchedule.IsDue(now, now, false, false));
    Assert(!BenchmarkSchedule.IsDue(now, null, true, false));
});
Check("missed schedule advances from now without replaying a backlog", () =>
{
    Assert(BenchmarkSchedule.NextDue(now.AddDays(20), new()) == now.AddDays(21));
});
Check("non-finite short and oversized intervals are refused", () =>
{
    foreach (var hours in new[] { double.NaN, double.PositiveInfinity, -1d, 0d, 0.09d, 8761d })
    {
        bool refused = false;
        try { BenchmarkSchedule.NextDue(now, new BenchmarkPreferences { IntervalHours = hours }); }
        catch (SafeOperationException) { refused = true; }
        Assert(refused);
    }
});

string Fixture() { var dir = Path.Combine(Path.GetTempPath(), "claudesafe-schedule-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
void Clean(string dir)
{
    if (!Path.GetFullPath(dir).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(dir).StartsWith("claudesafe-schedule-fixture-", StringComparison.Ordinal)) throw new Exception();
    foreach (var name in new[] { "settings.json", "source.json" }) { var path = Path.Combine(dir, name); if (File.Exists(path)) File.Delete(path); }
    Directory.Delete(dir);
}
Check("first use persists a future due date without starting or testing immediately", () =>
{
    var dir = Fixture();
    try
    {
        using var manager = new ManagerService(dir);
        var state = manager.BenchmarkSnapshotAsync().GetAwaiter().GetResult();
        Assert(!state.Busy && state.Run is null && state.NextDueUtc > DateTimeOffset.UtcNow.AddHours(23));
        Assert(!manager.SnapshotAsync().GetAwaiter().GetResult().Running);
        Assert(File.Exists(Path.Combine(dir, "settings.json")));
    }
    finally { Clean(dir); }
});
Check("editing interval persists due date and reopening does not reset it", () =>
{
    var dir = Fixture(); DateTimeOffset? saved;
    try
    {
        using (var manager = new ManagerService(dir))
        {
            manager.SaveBenchmarkPreferencesAsync(new BenchmarkPreferences { IntervalHours = 12.5 }).GetAwaiter().GetResult();
            var state = manager.BenchmarkSnapshotAsync().GetAwaiter().GetResult(); saved = state.NextDueUtc;
            Assert(saved > DateTimeOffset.UtcNow.AddHours(12.4) && saved < DateTimeOffset.UtcNow.AddHours(12.6));
        }
        using var reopened = new ManagerService(dir);
        Assert(reopened.BenchmarkSnapshotAsync().GetAwaiter().GetResult().NextDueUtc == saved);
    }
    finally { Clean(dir); }
});
Check("disabling schedule preserves explicit auto setting and prevents scheduling", () =>
{
    var dir = Fixture();
    try
    {
        using var manager = new ManagerService(dir);
        manager.SaveBenchmarkPreferencesAsync(new BenchmarkPreferences { ScheduleEnabled = false, AutoSwitchEnabled = true }).GetAwaiter().GetResult();
        var state = manager.BenchmarkSnapshotAsync().GetAwaiter().GetResult();
        Assert(state.NextDueUtc is null && !state.Preferences.ScheduleEnabled && state.Preferences.AutoSwitchEnabled);
        Assert(manager.SnapshotAsync().GetAwaiter().GetResult().AutoSwitchEnabled);
    }
    finally { Clean(dir); }
});
Check("parameter edit without an interval change keeps the due date", () =>
{
    var dir = Fixture();
    try
    {
        using var manager = new ManagerService(dir);
        var before = manager.BenchmarkSnapshotAsync().GetAwaiter().GetResult();
        before.Preferences.Rounds = 1;
        manager.SaveBenchmarkPreferencesAsync(before.Preferences).GetAwaiter().GetResult();
        Assert(manager.BenchmarkSnapshotAsync().GetAwaiter().GetResult().NextDueUtc == before.NextDueUtc);
    }
    finally { Clean(dir); }
});
Check("failed persistence cannot silently enable automation in memory", () =>
{
    var dir = Fixture();
    try
    {
        using var manager = new ManagerService(dir);
        var before = manager.BenchmarkSnapshotAsync().GetAwaiter().GetResult();
        using (var locked = new FileStream(Path.Combine(dir, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool refused = false;
            try { manager.SaveBenchmarkPreferencesAsync(new BenchmarkPreferences { AutoSwitchEnabled = true, IntervalHours = 1 }).GetAwaiter().GetResult(); }
            catch (SafeOperationException) { refused = true; }
            var after = manager.BenchmarkSnapshotAsync().GetAwaiter().GetResult();
            Assert(refused && !after.Preferences.AutoSwitchEnabled && after.NextDueUtc == before.NextDueUtc && after.Preferences.IntervalHours == 24);
        }
    }
    finally { Clean(dir); }
});
Console.WriteLine($"{passed} scheduler checks passed.");
