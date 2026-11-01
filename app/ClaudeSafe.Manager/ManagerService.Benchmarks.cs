using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSafe.Manager;

public sealed partial class ManagerService
{
    private readonly CancellationTokenSource benchmarkLifetime = new();
    private readonly object benchmarkProgressLock = new();
    private CancellationTokenSource? benchmarkRunCancellation;
    private Task? benchmarkTask;
    private Task? benchmarkScheduler;
    private BenchmarkRun? latestBenchmark;
    private string benchmarkMessage = "Desktop 实际资源测速：完整下载、响应类型、HTTP/1.1 与 HTTP/2。";
    private long configurationRevision;
    private long manualRevision;
    private DateTimeOffset lastBenchmarkProgressSavedUtc;
    private string BenchmarkDirectory => Path.Combine(DataDirectory, "benchmarks");
    private bool BenchmarkBusy => benchmarkTask is { IsCompleted: false };

    private void InitializeBenchmarks()
    {
        settings.Benchmark ??= new BenchmarkPreferences();
        DesktopBenchmarkTargets.ValidatePreferences(settings.Benchmark);
        // On a new installation start the first interval now, rather than testing immediately.
        if (!settings.Benchmark.ScheduleEnabled) settings.NextBenchmarkUtc = null;
        else settings.NextBenchmarkUtc ??= BenchmarkSchedule.NextDue(DateTimeOffset.UtcNow, settings.Benchmark);
        SaveSettings();
        var latest = Path.Combine(BenchmarkDirectory, "latest.json");
        if (File.Exists(latest))
        {
            try
            {
                if (new FileInfo(latest).Length > 32 * 1024 * 1024) throw new FormatException();
                latestBenchmark = JsonSerializer.Deserialize<BenchmarkRun>(File.ReadAllText(latest), ManagerJson.Options);
                if (latestBenchmark is not null && latestBenchmark.FinishedUtc is null)
                {
                    latestBenchmark.Status = "interrupted"; latestBenchmark.Cancelled = true;
                    latestBenchmark.Message = "上次测速因退出中断，保留样本但不用于自动切换。";
                }
            }
            catch { benchmarkMessage = "上次测速记录无法读取；不会据此自动切换。"; latestBenchmark = null; }
        }
        benchmarkScheduler = Task.Run(BenchmarkSchedulerAsync);
    }

    private async Task BenchmarkSchedulerAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(benchmarkLifetime.Token).ConfigureAwait(false))
            {
                await gate.WaitAsync(benchmarkLifetime.Token).ConfigureAwait(false);
                try
                {
                    if (disposed || benchmarkLifetime.IsCancellationRequested) return;
                    if (!BenchmarkSchedule.IsDue(DateTimeOffset.UtcNow, settings.NextBenchmarkUtc,
                        settings.Benchmark.ScheduleEnabled, BenchmarkBusy)) continue;
                    settings.NextBenchmarkUtc = BenchmarkSchedule.NextDue(DateTimeOffset.UtcNow, settings.Benchmark);
                    SaveSettings();
                    try { await QueueBenchmarkInsideGateAsync(false, "scheduled").ConfigureAwait(false); }
                    catch (SafeOperationException e) { benchmarkMessage = e.Message; }
                }
                catch { benchmarkMessage = "定时测速未能启动。请检查本机配置；生产通道没有因此变更。"; }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task<BenchmarkManagerSnapshot> BenchmarkSnapshotAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            BenchmarkRun? run;
            lock (benchmarkProgressLock) run = latestBenchmark is null ? null : DesktopBenchmarkCopies.Clone(latestBenchmark);
            return new BenchmarkManagerSnapshot { Preferences = DesktopBenchmarkCopies.Clone(settings.Benchmark),
                NextDueUtc = settings.NextBenchmarkUtc, Busy = BenchmarkBusy, Run = run, Message = benchmarkMessage };
        }
        finally { gate.Release(); }
    }

    public async Task SaveBenchmarkPreferencesAsync(BenchmarkPreferences preferences)
    {
        var candidate = DesktopBenchmarkCopies.Clone(preferences);
        DesktopBenchmarkTargets.ValidatePreferences(candidate);
        await gate.WaitAsync().ConfigureAwait(false);
        BenchmarkPreferences? previous = null; DateTimeOffset? previousDue = null;
        try
        {
            ThrowIfDisposed();
            previous = settings.Benchmark; previousDue = settings.NextBenchmarkUtc;
            bool resetClock = settings.Benchmark.ScheduleEnabled != candidate.ScheduleEnabled ||
                settings.Benchmark.IntervalHours != candidate.IntervalHours;
            settings.Benchmark = candidate;
            if (resetClock || candidate.ScheduleEnabled && settings.NextBenchmarkUtc is null)
                settings.NextBenchmarkUtc = BenchmarkSchedule.NextDue(DateTimeOffset.UtcNow, candidate);
            configurationRevision++;
            SaveSettings();
            benchmarkMessage = candidate.AutoSwitchEnabled
                ? "已启用定时自动切换；仅当当前节点未全通过时选择全通过最佳节点，无合格节点则 REJECT。"
                : "设置已保存。自动切换未启用；测速不会改变当前节点。";
        }
        catch (SafeOperationException) { throw; }
        catch
        {
            if (previous is not null) { settings.Benchmark = previous; settings.NextBenchmarkUtc = previousDue; }
            throw new SafeOperationException("测速设置无法保存。请检查本机目录权限。");
        }
        finally { gate.Release(); }
    }

    public async Task StartBenchmarkAsync(bool currentOnly = false)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { ThrowIfDisposed(); await QueueBenchmarkInsideGateAsync(currentOnly, "manual").ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task QueueBenchmarkInsideGateAsync(bool currentOnly, string trigger)
    {
        if (BenchmarkBusy) throw new SafeOperationException("已有测速任务，不能重叠运行。");
        if (benchmarkLifetime.IsCancellationRequested) throw new SafeOperationException("管理器正在退出。");
        if (IsRunning) await VerifyRuntimeAsync(false).ConfigureAwait(false);
        if (currentOnly && (!IsRunning || selected == "REJECT"))
            throw new SafeOperationException("当前通道未使用批准节点。请先手动选择节点，或测全部批准节点。");
        var frozenSettings = DesktopBenchmarkCopies.Clone(settings);
        var frozenNodes = nodes.Select(n => (JsonObject)n.DeepClone()).ToList();
        var eligible = CoreConfig.Summaries(frozenSettings, frozenNodes).Where(n => n.Approved && n.Eligible).ToList();
        if (eligible.Count == 0) throw new SafeOperationException("没有身份匹配且已批准的可测节点。");
        var frozenTargets = DesktopBenchmarkTargets.Load(DataDirectory);
        string targetFingerprint = JsonSerializer.Serialize(frozenTargets, ManagerJson.Options);
        string settingsFingerprint = BenchmarkInputFingerprint();
        var initialSelection = selected; var initialConfiguration = configurationRevision; var initialManual = manualRevision;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(benchmarkLifetime.Token);
        benchmarkRunCancellation = cancellation;
        var preparing = new BenchmarkRun { Id = Guid.NewGuid().ToString("N"), Status = "preparing", Trigger = trigger,
            StartedUtc = DateTimeOffset.UtcNow, Options = DesktopBenchmarkCopies.Clone(frozenSettings.Benchmark),
            Targets = frozenTargets, NodeNames = eligible.Where(n => !currentOnly || n.Name == selected).Select(n => n.Name).ToList(),
            Message = "正在准备独立测试通道；不改变生产节点。" };
        PublishBenchmark(preparing);
        benchmarkMessage = trigger == "scheduled" ? "定时全量测速运行中。" : "手动测速运行中；不会自动切换。";
        benchmarkTask = Task.Run(() => RunBenchmarkAsync(frozenSettings, frozenNodes, trigger, currentOnly ? initialSelection : null,
            initialSelection, initialConfiguration, initialManual, settingsFingerprint, targetFingerprint, cancellation));
    }

    private async Task RunBenchmarkAsync(ManagerSettings frozenSettings, List<JsonObject> frozenNodes, string trigger, string? onlyNode,
        string initialSelection, long initialConfiguration, long initialManual, string inputFingerprint, string targetFingerprint,
        CancellationTokenSource cancellation)
    {
        BenchmarkRun run;
        try
        {
            var progress = new BenchmarkProgress(PublishBenchmark);
            run = await new DesktopBenchmarkRunner().RunAsync(DataDirectory, frozenSettings, frozenNodes,
                frozenSettings.Benchmark, trigger, onlyNode, progress, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            lock (benchmarkProgressLock) run = latestBenchmark is null ? new BenchmarkRun() : DesktopBenchmarkCopies.Clone(latestBenchmark);
            run.Status = cancellation.IsCancellationRequested ? "cancelled" : "error";
            run.Cancelled = cancellation.IsCancellationRequested; run.FinishedUtc = DateTimeOffset.UtcNow;
            run.Message = run.Cancelled ? "测速已取消；保留已有样本，不触发自动切换。" : "测速未能完成；请检查内核、网卡及目标清单。";
        }
        try
        {
            await CompleteBenchmarkAsync(run, initialSelection, initialConfiguration, initialManual, inputFingerprint, targetFingerprint, cancellation.Token).ConfigureAwait(false);
        }
        finally { cancellation.Dispose(); }
    }

    private async Task CompleteBenchmarkAsync(BenchmarkRun run, string initialSelection, long initialConfiguration, long initialManual,
        string inputFingerprint, string targetFingerprint, CancellationToken cancellation)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Cancellation can win the gate after the runner has already returned completed.
            if (cancellation.IsCancellationRequested)
            {
                run.Cancelled = true; run.Status = "cancelled";
                run.Message = "已取消本次测速的后续处理；不会自动切换。";
            }
            bool persisted = TrySaveBenchmark(run);
            if (!disposed && !benchmarkLifetime.IsCancellationRequested && persisted)
            {
                bool attemptedControlChange = false;
                try
                {
                    // Manual tests and disabled automation must not depend on a production-controller read.
                    if (run.Trigger == "scheduled" && settings.Benchmark.AutoSwitchEnabled && IsRunning)
                        await VerifyRuntimeAsync(false).ConfigureAwait(false);
                    bool targetsUnchanged;
                    try
                    {
                        targetsUnchanged = JsonSerializer.Serialize(DesktopBenchmarkTargets.Load(DataDirectory), ManagerJson.Options) == targetFingerprint &&
                            JsonSerializer.Serialize(run.Targets, ManagerJson.Options) == targetFingerprint;
                    }
                    catch { targetsUnchanged = false; }
                    var expectedNames = CoreConfig.Summaries(settings, nodes).Where(n => n.Approved && n.Eligible).Select(n => n.Name).Order(StringComparer.Ordinal);
                    bool fullApprovedSet = run.NodeNames.Order(StringComparer.Ordinal).SequenceEqual(expectedNames);
                    bool unchanged = configurationRevision == initialConfiguration && BenchmarkInputFingerprint() == inputFingerprint && targetsUnchanged && fullApprovedSet &&
                        JsonSerializer.Serialize(run.Options, ManagerJson.Options) == JsonSerializer.Serialize(settings.Benchmark, ManagerJson.Options);
                    var decision = BenchmarkAutoPolicy.Decide(run, selected, IsRunning, settings.Benchmark.AutoSwitchEnabled,
                        unchanged, manualRevision == initialManual && selected == initialSelection);
                    run.AutoSwitchOutcome = decision.Reason;
                    if (decision.Action is "switch" or "reject")
                    {
                        var target = decision.Action == "reject" ? "REJECT" : decision.NodeName!;
                        if (target != "REJECT" && !CoreConfig.Summaries(settings, nodes).Any(n => n.Name == target && n.Approved && n.Eligible))
                            throw new SafeOperationException("候选节点身份已变化。");
                        attemptedControlChange = true;
                        await ApiAsync(HttpMethod.Put, "proxies/" + Uri.EscapeDataString(CoreConfig.Group),
                            new JsonObject { ["name"] = target }).ConfigureAwait(false);
                        await ApiAsync(HttpMethod.Delete, "connections").ConfigureAwait(false);
                        await VerifyRuntimeAsync(false).ConfigureAwait(false);
                        if (selected != target) throw new FormatException();
                        manualRevision++;
                        run.AutoSwitchOutcome = decision.Action == "reject" ? "已切换 REJECT。" + decision.Reason : "已切换至「" + target + "」。" + decision.Reason;
                        message = decision.Action == "reject" ? "定时测速没有全通过节点，已阻断专用通道。" : "定时测速后已切换到全通过最佳节点。";
                    }
                }
                catch
                {
                    if (attemptedControlChange)
                    {
                        // A bad response after a control mutation must not leave uncertain forwarding state.
                        await StopCoreAsync().ConfigureAwait(false);
                        run.AutoSwitchOutcome = "切换未通过安全核对，已停止专用内核。";
                    }
                    else run.AutoSwitchOutcome = "结果或配置未通过自动切换核对，未改变当前通道。";
                }
            }
            else run.AutoSwitchOutcome = persisted ? "管理器正在退出；未改变通道。" : "结果无法持久保存；未执行自动切换。";
            benchmarkMessage = run.Message + " " + run.AutoSwitchOutcome;
            // A slow run may span more than an interval; don't replay an overdue backlog.
            if (!disposed && run.Trigger == "scheduled" && settings.Benchmark.ScheduleEnabled && settings.NextBenchmarkUtc <= DateTimeOffset.UtcNow)
            {
                settings.NextBenchmarkUtc = BenchmarkSchedule.NextDue(DateTimeOffset.UtcNow, settings.Benchmark);
                try { SaveSettings(); } catch { benchmarkMessage += " 下次测试时间未能保存。"; }
            }
            TrySaveBenchmark(run); PublishBenchmark(run);
            benchmarkRunCancellation = null;
        }
        finally { gate.Release(); }
    }

    public async Task CancelBenchmarkAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (benchmarkRunCancellation is not null) { manualRevision++; benchmarkRunCancellation.Cancel(); }
            benchmarkMessage = "正在取消测速；生产节点保持当前选择。";
        }
        finally { gate.Release(); }
    }

    private string BenchmarkInputFingerprint()
    {
        var text = JsonSerializer.Serialize(new { settings.Pins, settings.InterfaceName, settings.CorePath, settings.CoreSha256,
            settings.Benchmark, Nodes = nodes }, ManagerJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private void PublishBenchmark(BenchmarkRun run)
    {
        var copy = DesktopBenchmarkCopies.Clone(run);
        bool saveProgress = false;
        lock (benchmarkProgressLock)
        {
            if (latestBenchmark?.Id == copy.Id && latestBenchmark.Completed > copy.Completed) return;
            latestBenchmark = copy;
            if (copy.FinishedUtc is null && DateTimeOffset.UtcNow - lastBenchmarkProgressSavedUtc > TimeSpan.FromSeconds(5))
            { lastBenchmarkProgressSavedUtc = DateTimeOffset.UtcNow; saveProgress = true; }
        }
        if (saveProgress) TrySaveBenchmark(copy);
    }

    private bool TrySaveBenchmark(BenchmarkRun run)
    {
        try
        {
            Directory.CreateDirectory(BenchmarkDirectory);
            var contents = JsonSerializer.Serialize(run, ManagerJson.Options);
            AtomicWrite(Path.Combine(BenchmarkDirectory, "latest.json"), contents);
            // Use an application-owned GUID name, never a source URL or node label as a filesystem path.
            if (run.FinishedUtc.HasValue)
                AtomicWrite(Path.Combine(BenchmarkDirectory, "run-" + run.StartedUtc.ToString("yyyyMMddTHHmmssfff") + "-" +
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(run.Id)))[..12] + ".json"), contents);
            return true;
        }
        catch { return false; }
    }

    private sealed class BenchmarkProgress(Action<BenchmarkRun> report) : IProgress<BenchmarkRun>
    { public void Report(BenchmarkRun value) => report(value); }
}
