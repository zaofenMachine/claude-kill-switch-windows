using ClaudeSafe.Manager;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
int passed = 0;
void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { Console.Error.WriteLine("FAIL " + name + ": " + e.Message); Environment.ExitCode = 1; }
}
void Assert(bool condition) { if (!condition) throw new Exception("Unexpected policy decision."); }
BenchmarkAutoDecision Decide(BenchmarkRun run, string current = "Current", bool running = true,
    bool enabled = true, bool unchanged = true, bool manualUnchanged = true) =>
    BenchmarkAutoPolicy.Decide(run, current, running, enabled, unchanged, manualUnchanged);
BenchmarkRun Run(string[]? names = null, string protocol = "both", int rounds = 2)
{
    var options = new BenchmarkPreferences { Protocol = protocol, Rounds = rounds, TimeoutSeconds = 30,
        Concurrency = 2, IntervalHours = 24, ScheduleEnabled = true, AutoSwitchEnabled = true };
    var targets = new List<BenchmarkTarget>
    {
        new() { Id = "module", Label = "Fixture module", Kind = "resource", ExpectedType = "text/javascript",
            Url = "https://assets-proxy.anthropic.com/claude-ai/v2/assets/v1/fixture.js" },
        new() { Id = "site", Label = "Site", Kind = "endpoint", Url = "https://claude.ai/" },
        new() { Id = "api", Label = "API", Kind = "endpoint", Url = "https://api.anthropic.com/" }
    };
    var run = new BenchmarkRun { Id = "fixture", Trigger = "scheduled", Status = "completed",
        StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-1), FinishedUtc = DateTimeOffset.UtcNow,
        Options = options, Targets = targets, NodeNames = (names ?? ["Current", "Best", "Other"]).ToList() };
    var protocols = protocol == "both" ? new[] { "http/1.1", "h2" } : new[] { protocol };
    foreach (var name in run.NodeNames)
    foreach (var target in targets)
    foreach (var requested in protocols)
    for (var round = 1; round <= rounds; round++)
        run.Samples.Add(new BenchmarkSample { Name = name, TargetId = target.Id, Protocol = requested, ActualProtocol = requested,
            Round = round, Http = target.Id == "api" ? 404 : 200, Complete = true, TlsVerified = true,
            ResourceValid = target.Kind == "resource", ContentType = target.Kind == "resource" ? "text/javascript" : "text/html",
            Bytes = 100, ExpectedBytes = 100, TotalMs = name == "Best" ? 100 : name == "Other" ? 200 : 300,
            TtfbMs = 80, Status = "ok" });
    Finish(run); return run;
}
void Finish(BenchmarkRun run)
{
    var protocols = run.Options.Protocol == "both" ? new[] { "http/1.1", "h2" } : new[] { run.Options.Protocol };
    run.Total = run.NodeNames.Count * run.Targets.Count * protocols.Length * run.Options.Rounds;
    run.Completed = run.Samples.Count;
    run.Ranks = run.NodeNames.Select(name => DesktopBenchmarkRanker.Rank(name, run.Targets,
        protocols, run.Options.Rounds, run.Samples)).ToList();
}
void FailNode(BenchmarkRun run, string name)
{
    var sample = run.Samples.First(s => s.Name == name);
    sample.Complete = false; sample.ResourceValid = false; sample.Status = "incomplete"; sample.Bytes = 40;
    Finish(run);
}
void FailFastNode(BenchmarkRun run, string name, int failedSlot = 0)
{
    run.Options.StopOnFirstFailure = true;
    var protocols = DesktopBenchmarkTargets.Protocols(run.Options);
    var ordered = (from round in Enumerable.Range(1, run.Options.Rounds)
        from target in run.Targets from protocol in protocols
        select run.Samples.Single(s => s.Name == name && s.Round == round && s.TargetId == target.Id && s.Protocol == protocol)).ToList();
    var failure = ordered[failedSlot]; failure.Complete = false; failure.Status = "incomplete"; failure.Bytes = 40;
    foreach (var sample in ordered.Skip(failedSlot + 1))
        run.Samples[run.Samples.IndexOf(sample)] = DesktopBenchmarkSamples.CreateSkipped(name,
            run.Targets.Single(t => t.Id == sample.TargetId), sample.Protocol, sample.Round);
    Finish(run);
}

Check("automatic switching defaults off and interval defaults to 24h", () =>
{
    var defaults = new BenchmarkPreferences(); Assert(!defaults.AutoSwitchEnabled && !defaults.StopOnFirstFailure && defaults.IntervalHours == 24);
});
Check("disabled automation preserves current even if it failed", () =>
{
    var run = Run(); FailNode(run, "Current"); Assert(Decide(run, enabled: false).Action == "keep");
});
Check("manual benchmark never switches", () =>
{
    var run = Run(); FailNode(run, "Current"); run.Trigger = "manual"; Assert(Decide(run).Action == "keep");
});
Check("healthy current stays despite a faster best node", () => Assert(Decide(Run()).Action == "keep"));
Check("failed current switches to fastest fully passing node", () =>
{
    var run = Run(); FailNode(run, "Current"); var decision = Decide(run);
    Assert(decision.Action == "switch" && decision.NodeName == "Best");
});
Check("partial faster candidate never displaces a fully passing candidate", () =>
{
    var run = Run(); FailNode(run, "Current"); FailNode(run, "Best");
    Assert(Decide(run).Action == "switch" && Decide(run).NodeName == "Other");
});
Check("best candidate uses worst target median before a faster average", () =>
{
    var run = Run(); FailNode(run, "Current");
    foreach (var sample in run.Samples.Where(s => s.Name == "Best")) sample.TotalMs = sample.TargetId == "module" ? 400 : 50;
    foreach (var sample in run.Samples.Where(s => s.Name == "Other")) sample.TotalMs = 300;
    Finish(run); Assert(Decide(run).NodeName == "Other");
});
Check("equal passing candidates use stable natural node order", () =>
{
    var run = Run(["Current", "节点10", "节点2"]); FailNode(run, "Current");
    Assert(Decide(run).NodeName == "节点2");
});
Check("no fully passing candidate requests REJECT", () =>
{
    var run = Run(); foreach (var name in run.NodeNames) FailNode(run, name);
    var result = Decide(run); Assert(result.Action == "reject" && result.NodeName == "REJECT");
});
Check("manual REJECT is never automatically reopened", () => Assert(Decide(Run(), current: "REJECT").Action == "keep"));
Check("stopped production is not automatically started", () =>
{
    var run = Run(); FailNode(run, "Current"); Assert(Decide(run, running: false).Action == "keep");
});
Check("configuration changes invalidate an automatic decision", () =>
{
    var run = Run(); FailNode(run, "Current"); Assert(Decide(run, unchanged: false).Action == "keep");
});
Check("manual selection during benchmark wins over automatic result", () =>
{
    var run = Run(); FailNode(run, "Current"); Assert(Decide(run, manualUnchanged: false).Action == "keep");
});
Check("cancelled errored or unfinished runs do not switch", () =>
{
    var cancelled = Run(); cancelled.Cancelled = true; Assert(Decide(cancelled).Action == "keep");
    var error = Run(); error.Status = "error"; Assert(Decide(error).Action == "keep");
    var unfinished = Run(); unfinished.FinishedUtc = null; Assert(Decide(unfinished).Action == "keep");
    var running = Run(); running.Status = "running"; Assert(Decide(running).Action == "keep");
});
Check("stale resource status prevents switching", () =>
{
    var run = Run(); FailNode(run, "Current"); run.Samples.First().Status = "target_stale";
    Assert(Decide(run).Action == "keep");
});
Check("resource 404 and 410 are stale even with an incorrect status label", () =>
{
    foreach (var code in new[] { 404, 410 })
    { var run = Run(); run.Samples.First(s => s.TargetId == "module").Http = code; Assert(Decide(run).Action == "keep"); }
});
Check("anonymous API root 404 remains passing", () => Assert(Decide(Run()).Action == "keep"));
Check("complete endpoint redirects remain passing like the web tester", () =>
{
    var run = Run(); foreach (var sample in run.Samples.Where(s => s.TargetId == "site")) { sample.Http = 302; sample.Status = "redirect"; }
    FailNode(run, "Best"); Assert(Decide(run).Action == "keep");
});
Check("TLS failure truncated body and wrong resource type invalidate current node", () =>
{
    foreach (var kind in new[] { "tls", "bytes", "mime" })
    {
        var run = Run(); var sample = run.Samples.First(s => s.Name == "Current" && s.TargetId == "module");
        if (kind == "tls") sample.TlsVerified = false;
        if (kind == "bytes") sample.Bytes = 40; // Complete/ResourceValid claims cannot override a short body.
        if (kind == "mime") sample.ContentType = "text/html";
        Finish(run); Assert(Decide(run).Action == "switch" && Decide(run).NodeName == "Best");
    }
});
Check("HTTP2 fallback and blocked API responses do not pass current node", () =>
{
    var fallback = Run(); fallback.Samples.First(s => s.Name == "Current" && s.Protocol == "h2").ActualProtocol = "http/1.1";
    Finish(fallback); Assert(Decide(fallback).NodeName == "Best");
    foreach (var code in new[] { 401, 403, 429, 500, 503 })
    {
        var run = Run(); run.Samples.First(s => s.Name == "Current" && s.TargetId == "api").Http = code;
        Finish(run); Assert(Decide(run).NodeName == "Best");
    }
});
Check("missing sample does not become node failure evidence", () =>
{
    var run = Run(); FailNode(run, "Current"); run.Samples.RemoveAt(0); Assert(Decide(run).Action == "keep");
});
Check("duplicates cannot fill a missing slot", () =>
{
    var run = Run(); FailNode(run, "Current"); run.Samples[0] = run.Samples[1]; Assert(Decide(run).Action == "keep");
});
Check("extra unknown node target protocol or round invalidates matrix", () =>
{
    foreach (var field in new[] { "name", "target", "protocol", "round" })
    {
        var run = Run(); FailNode(run, "Current"); var sample = run.Samples[0];
        if (field == "name") sample.Name = "Unknown";
        if (field == "target") sample.TargetId = "unknown";
        if (field == "protocol") sample.Protocol = "h3";
        if (field == "round") sample.Round = 0;
        Assert(Decide(run).Action == "keep");
    }
});
Check("cached passing flags never override failed underlying samples", () =>
{
    var run = Run(); foreach (var name in run.NodeNames) FailNode(run, name);
    foreach (var rank in run.Ranks) rank.AllPassed = true;
    Assert(Decide(run).Action == "reject");
});
Check("cached failing flags never force a healthy current away", () =>
{
    var run = Run(); foreach (var rank in run.Ranks) rank.AllPassed = false;
    Assert(Decide(run).Action == "keep");
});
Check("cached scores cannot reorder actual fully passing candidates", () =>
{
    var run = Run(); FailNode(run, "Current");
    run.Ranks.Single(r => r.Name == "Best").WorstMedianMs = 99999;
    run.Ranks.Single(r => r.Name == "Other").WorstMedianMs = 1;
    Assert(Decide(run).NodeName == "Best");
});
Check("missing current node or missing finalized rank does not switch", () =>
{
    var run = Run(); FailNode(run, "Current"); Assert(Decide(run, current: "Absent").Action == "keep");
    run.Ranks.RemoveAll(r => r.Name == "Current"); Assert(Decide(run).Action == "keep");
});
Check("empty nodes or targets are never a usable automatic run", () =>
{
    var noNodes = Run([]); Assert(Decide(noNodes).Action == "keep");
    var noTargets = Run(); noTargets.Targets.Clear(); noTargets.Samples.Clear(); Finish(noTargets); Assert(Decide(noTargets).Action == "keep");
});
Check("duplicate node names or target identifiers are refused", () =>
{
    var nodes = Run(); nodes.NodeNames.Add("Current"); Assert(Decide(nodes).Action == "keep");
    var targets = Run(); targets.Targets.Add(targets.Targets[0]); Assert(Decide(targets).Action == "keep");
});
Check("completion counters must match requested matrix", () =>
{
    var run = Run(); FailNode(run, "Current"); run.Completed--; Assert(Decide(run).Action == "keep");
    run.Completed++; run.Total++; Assert(Decide(run).Action == "keep");
});
Check("invalid benchmark options are refused", () =>
{
    foreach (var kind in new[] { "rounds", "timeout", "concurrency", "interval", "nan", "protocol" })
    {
        var run = Run(); FailNode(run, "Current");
        if (kind == "rounds") run.Options.Rounds = 0;
        if (kind == "timeout") run.Options.TimeoutSeconds = 0;
        if (kind == "concurrency") run.Options.Concurrency = 0;
        if (kind == "interval") run.Options.IntervalHours = 0;
        if (kind == "nan") run.Options.IntervalHours = double.NaN;
        if (kind == "protocol") run.Options.Protocol = "unknown";
        Assert(Decide(run).Action == "keep");
    }
});
Check("valid fail-fast suffixes after an actual failed request permit the best healthy node", () =>
{
    foreach (var failedSlot in new[] { 0, 3, 6, 10, 11 })
    {
        var run = Run(); FailFastNode(run, "Current", failedSlot);
        var decision = Decide(run); Assert(decision.Action == "switch" && decision.NodeName == "Best");
    }
});
Check("fail-fast failures for every node request REJECT without treating skips as passes", () =>
{
    var run = Run(); FailFastNode(run, "Current", 0); FailFastNode(run, "Best", 2); FailFastNode(run, "Other", 7);
    var decision = Decide(run); Assert(decision.Action == "reject" && decision.NodeName == "REJECT");
});
Check("skipped fastest candidate is never selected even if its cached rank claims success", () =>
{
    var run = Run(); FailFastNode(run, "Current"); FailFastNode(run, "Best");
    run.Ranks.Single(r => r.Name == "Best").AllPassed = true;
    Assert(Decide(run).Action == "switch" && Decide(run).NodeName == "Other");
});
Check("fail-fast enabled still preserves a healthy current node", () =>
{
    var run = Run(); run.Options.StopOnFirstFailure = true; FailFastNode(run, "Best");
    Assert(Decide(run).Action == "keep");
});
Check("skips require the enabled preference and an actual preceding failure", () =>
{
    var disabled = Run(); FailFastNode(disabled, "Current"); disabled.Options.StopOnFirstFailure = false;
    Assert(Decide(disabled).Action == "keep");
    var noFailure = Run(); FailFastNode(noFailure, "Current");
    var actual = noFailure.Samples.Single(s => s.Name == "Current" && !DesktopBenchmarkSamples.IsSkipped(s));
    noFailure.Samples[noFailure.Samples.IndexOf(actual)] = DesktopBenchmarkSamples.CreateSkipped(actual.Name,
        noFailure.Targets.Single(t => t.Id == actual.TargetId), actual.Protocol, actual.Round);
    Finish(noFailure); Assert(Decide(noFailure).Action == "keep");
    var passedPrefix = Run(); FailFastNode(passedPrefix, "Current", 2);
    actual = passedPrefix.Samples.First(s => s.Name == "Current" && !s.Complete && !DesktopBenchmarkSamples.IsSkipped(s));
    actual.Complete = true; actual.Bytes = 100; actual.Status = "ok";
    Finish(passedPrefix); Assert(Decide(passedPrefix).Action == "keep");
});
Check("missing duplicate and interrupted skip suffixes cannot drive an automatic action", () =>
{
    foreach (var invalid in new[] { "missing", "duplicate", "attempt-after-failure" })
    {
        var run = Run(); FailFastNode(run, "Current");
        var skips = run.Samples.Where(s => s.Name == "Current" && DesktopBenchmarkSamples.IsSkipped(s)).ToList();
        if (invalid == "missing") run.Samples.Remove(skips[0]);
        if (invalid == "duplicate") run.Samples[run.Samples.IndexOf(skips[0])] = skips[1];
        if (invalid == "attempt-after-failure")
        {
            var sample = skips[0]; sample.Status = "network_error"; sample.Error = "真实请求失败。";
        }
        Finish(run); Assert(Decide(run).Action == "keep");
    }
});
Check("skipped records cannot carry response evidence or altered status semantics", () =>
{
    foreach (var field in new[] { "http", "complete", "tls", "resource", "bytes", "expected", "ttfb", "total", "actual-protocol", "type", "status", "reason" })
    {
        var run = Run(); FailFastNode(run, "Current"); var sample = run.Samples.First(DesktopBenchmarkSamples.IsSkipped);
        if (field == "http") sample.Http = 200;
        if (field == "complete") sample.Complete = true;
        if (field == "tls") sample.TlsVerified = true;
        if (field == "resource") sample.ResourceValid = true;
        if (field == "bytes") sample.Bytes = 100;
        if (field == "expected") sample.ExpectedBytes = 0;
        if (field == "ttfb") sample.TtfbMs = 0;
        if (field == "total") sample.TotalMs = 1;
        if (field == "actual-protocol") sample.ActualProtocol = sample.Protocol;
        if (field == "type") sample.ContentType = "text/javascript";
        if (field == "status") sample.Status = "Skipped";
        if (field == "reason") sample.Error = "";
        Assert(Decide(run).Action == "keep");
    }
});
Check("an enabled run with attempted slots after its first failure is incompatible evidence", () =>
{
    var run = Run(); FailNode(run, "Current"); run.Options.StopOnFirstFailure = true;
    Assert(Decide(run).Action == "keep");
});
Check("fail-fast cancellation or manual invocation never applies a valid skip matrix", () =>
{
    foreach (var invalid in new[] { "manual", "cancelled", "error", "cancelled-sample" })
    {
        var run = Run(); FailFastNode(run, "Current");
        if (invalid == "manual") run.Trigger = "manual";
        if (invalid == "cancelled") run.Cancelled = true;
        if (invalid == "error") run.Status = "error";
        if (invalid == "cancelled-sample") run.Samples.First(s => s.Name == "Current" && !DesktopBenchmarkSamples.IsSkipped(s)).Status = "cancelled";
        Assert(Decide(run).Action == "keep");
    }
});

Console.WriteLine($"{passed} automatic benchmark policy checks passed.");
