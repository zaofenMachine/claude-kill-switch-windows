namespace ClaudeSafe.Manager;

public sealed class BenchmarkAutoDecision
{
    public string Action { get; init; } = "keep";
    public string? NodeName { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// A decision only: this class never changes the live selector or starts a core.
/// Incomplete runs, stale resource targets and user changes cannot cause an automatic switch.
/// </summary>
public static class BenchmarkAutoPolicy
{
    public static BenchmarkAutoDecision Decide(BenchmarkRun run, string currentNode,
        bool productionRunning, bool autoEnabled, bool configurationUnchanged, bool manualSelectionUnchanged)
    {
        if (!autoEnabled) return Keep("自动切换未启用，保留当前节点。");
        if (!productionRunning) return Keep("专用内核未运行，不自动启动或选择节点。");
        if (string.IsNullOrWhiteSpace(currentNode) || currentNode.Equals("REJECT", StringComparison.OrdinalIgnoreCase))
            return Keep("当前通道处于阻断状态，不自动恢复手动阻断。");
        if (!configurationUnchanged) return Keep("测速期间配置发生变化，本次不自动切换。");
        if (!manualSelectionUnchanged) return Keep("测速期间已有手动选择或停止操作，保留该操作。");
        if (run is null || run.Trigger != "scheduled") return Keep("手动测速只更新结果，不自动切换。");
        if (run.Status != "completed" || run.FinishedUtc is null || run.Cancelled)
            return Keep("测速尚未完整结束或已取消，本次不自动切换。");

        try
        {
            if (!ValidPreferences(run.Options)) return Keep("测速参数无效，本次不自动切换。");
            var protocols = Protocols(run.Options.Protocol);
            if (!HasExactMatrix(run, protocols)) return Keep("测速结果不完整或存在重复、未知样本，本次不自动切换。");

            // A changed public resource URL is a manifest-maintenance problem, not evidence
            // that every approved exit is unusable. Keep the current choice until it is reviewed.
            var targets = run.Targets.ToDictionary(t => t.Id, StringComparer.Ordinal);
            if (run.Samples.Any(s => targets[s.TargetId].Kind == "resource" &&
                (s.Status == "target_stale" || s.Http is 404 or 410)))
                return Keep("公开资源目标已失效，需要更新测试清单；本次不自动切换。");

            if (!run.NodeNames.Contains(currentNode, StringComparer.Ordinal) ||
                !run.Ranks.Any(r => r.Name == currentNode))
                return Keep("本次没有当前节点的完整结果，本次不自动切换。");

            // Recompute from every requested slot. Persisted AllPassed/ranking flags are
            // presentation data and are never sufficient evidence for changing a live proxy.
            var ranks = run.NodeNames.Select(name => DesktopBenchmarkRanker.Rank(
                name, run.Targets, protocols, run.Options.Rounds, run.Samples)).ToList();
            var current = ranks.Single(r => r.Name == currentNode);
            if (current.AllPassed) return Keep("当前节点全部通过，继续使用；不因其他节点更快而切换。");

            var candidates = ranks.Where(r => r.AllPassed).ToList();
            candidates.Sort(DesktopBenchmarkRanker.Compare);
            if (candidates.Count == 0)
                return new BenchmarkAutoDecision { Action = "reject", NodeName = "REJECT",
                    Reason = "当前节点未全部通过，且没有全部通过的批准候选，需要阻断专用通道。" };
            return new BenchmarkAutoDecision { Action = "switch", NodeName = candidates[0].Name,
                Reason = "当前节点未全部通过，选择全部通过的批准节点中排名最佳者。" };
        }
        catch
        {
            // Malformed or incompatible saved data must never turn into a live action.
            return Keep("测速结果格式无效，本次不自动切换。");
        }
    }

    private static BenchmarkAutoDecision Keep(string reason) => new() { Reason = reason };

    private static bool ValidPreferences(BenchmarkPreferences preferences) => preferences is not null &&
        preferences.Rounds is >= 1 and <= 10 && preferences.TimeoutSeconds is >= 3 and <= 60 &&
        preferences.Concurrency is >= 1 and <= 4 && double.IsFinite(preferences.IntervalHours) &&
        preferences.IntervalHours is >= 0.1 and <= 8760 &&
        preferences.Protocol is "http/1.1" or "h2" or "both";

    private static string[] Protocols(string protocol) => protocol switch
    {
        "http/1.1" => ["http/1.1"],
        "h2" => ["h2"],
        "both" => ["http/1.1", "h2"],
        _ => []
    };

    private static bool HasExactMatrix(BenchmarkRun run, IReadOnlyList<string> protocols)
    {
        if (run.NodeNames is null || run.Targets is null || run.Samples is null || run.Ranks is null ||
            run.NodeNames.Count is < 1 or > 4096 || run.Targets.Count is < 1 or > 16 || protocols.Count == 0)
            return false;
        if (run.NodeNames.Any(string.IsNullOrWhiteSpace) ||
            run.NodeNames.Any(n => CoreConfig.ReservedNames.Contains(n)) ||
            run.NodeNames.Distinct(StringComparer.Ordinal).Count() != run.NodeNames.Count ||
            run.Targets.Any(t => t is null || string.IsNullOrWhiteSpace(t.Id) ||
                t.Kind is not ("resource" or "endpoint")) ||
            run.Targets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != run.Targets.Count)
            return false;

        int expected = checked(run.NodeNames.Count * run.Targets.Count * protocols.Count * run.Options.Rounds);
        if (run.Total != expected || run.Completed != expected || run.Samples.Count != expected)
            return false;
        // The runner must have finalized a rank for every tested node, without extras.
        // Their cached pass flags are deliberately ignored below.
        if (run.Ranks.Count != run.NodeNames.Count ||
            run.Ranks.Any(r => r is null || !run.NodeNames.Contains(r.Name, StringComparer.Ordinal)) ||
            run.Ranks.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != run.NodeNames.Count)
            return false;

        var names = run.NodeNames.ToHashSet(StringComparer.Ordinal);
        var targetIds = run.Targets.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var slots = new Dictionary<(string Name, string Target, string Protocol, int Round), BenchmarkSample>();
        foreach (var sample in run.Samples)
        {
            if (sample is null || !names.Contains(sample.Name) || !targetIds.Contains(sample.TargetId) ||
                !protocols.Contains(sample.Protocol, StringComparer.Ordinal) ||
                sample.Round < 1 || sample.Round > run.Options.Rounds ||
                !slots.TryAdd((sample.Name, sample.TargetId, sample.Protocol, sample.Round), sample))
                return false;
        }
        if (slots.Count != expected) return false;

        // Fail-fast resolves the full plan with explicit skipped records. They are valid only
        // as an exact suffix after this node's first actual failed request in runner order.
        // A missing request, a skipped healthy node, or an attempted slot after that failure
        // is incompatible evidence and cannot drive an automatic action.
        foreach (var name in run.NodeNames)
        {
            bool failureSeen = false;
            for (int round = 1; round <= run.Options.Rounds; round++)
            foreach (var target in run.Targets)
            foreach (var protocol in protocols)
            {
                var sample = slots[(name, target.Id, protocol, round)];
                if (DesktopBenchmarkSamples.IsSkipped(sample))
                {
                    if (!run.Options.StopOnFirstFailure || !failureSeen || !DesktopBenchmarkSamples.IsCanonicalSkipped(sample))
                        return false;
                }
                else
                {
                    if (sample.Status == "cancelled" || run.Options.StopOnFirstFailure && failureSeen) return false;
                    if (!DesktopBenchmarkRanker.IsPassing(sample, target)) failureSeen = true;
                }
            }
        }
        return true;
    }
}
