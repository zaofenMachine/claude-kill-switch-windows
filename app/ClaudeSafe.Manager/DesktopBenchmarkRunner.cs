using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeSafe.Manager;

public sealed class DesktopBenchmarkRunner
{
    public async Task<BenchmarkRun> RunAsync(string dataDirectory, ManagerSettings settings, List<JsonObject> nodes,
        BenchmarkPreferences options, string trigger, string? onlyNodeName, IProgress<BenchmarkRun>? progress,
        CancellationToken cancel)
    {
        var run = new BenchmarkRun { Id = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8],
            Status = "preparing", Trigger = trigger, StartedUtc = DateTimeOffset.UtcNow };
        var sync = new object();
        Process? core = null; OwnedJob? job = null; string? directory = null;
        List<TcpListener> reservations = [];
        string[] protocols = [];
        void Report()
        {
            if (progress is null) return;
            BenchmarkRun snapshot;
            lock (sync) snapshot = DesktopBenchmarkCopies.Clone(run);
            try { progress.Report(snapshot); } catch { /* A closed observer must not bypass cleanup. */ }
        }
        void UpdateRanks()
        {
            run.Ranks = run.NodeNames.Select(n => DesktopBenchmarkRanker.Rank(n, run.Targets, protocols, run.Options.Rounds, run.Samples)).ToList();
            run.Ranks.Sort(DesktopBenchmarkRanker.Compare);
        }
        try
        {
            cancel.ThrowIfCancellationRequested();
            settings = DesktopBenchmarkCopies.Clone(settings);
            options = DesktopBenchmarkCopies.Clone(options);
            DesktopBenchmarkTargets.ValidatePreferences(options); protocols = DesktopBenchmarkTargets.Protocols(options);
            run.Options = options; run.Targets = DesktopBenchmarkTargets.Load(dataDirectory);
            foreach (var target in run.Targets) DesktopBenchmarkTargets.Validate(target);
            var source = CoreConfig.ParseSource(new JsonObject { ["proxies"] = new JsonArray(nodes.Select(n => n.DeepClone()).ToArray()) }.ToJsonString(ManagerJson.Options));
            var approved = CoreConfig.Summaries(settings, source).Where(n => n.Approved && n.Eligible).Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
            if (onlyNodeName is not null)
            {
                if (!approved.Contains(onlyNodeName)) throw new SafeOperationException("当前节点不是可测试的批准节点；REJECT 不参与测速。");
                approved.IntersectWith([onlyNodeName]);
            }
            if (approved.Count == 0) throw new SafeOperationException("没有身份匹配的批准节点可供测试。");
            if (approved.Count > 128) throw new SafeOperationException("单次测速最多支持 128 个批准节点，请分批维护节点来源。");
            var leaves = source.Where(n => approved.Contains(CoreConfig.Identity(n).Name)).ToList();
            run.NodeNames = leaves.Select(n => CoreConfig.Identity(n).Name).ToList();
            run.Total = checked(run.NodeNames.Count * run.Targets.Count * protocols.Length * options.Rounds);
            lock (sync) UpdateRanks(); Report();
            if (!ManagerService.PhysicalInterfaces().Contains(settings.InterfaceName, StringComparer.Ordinal))
                throw new SafeOperationException("选定的真实物理网卡不可用，测速不会改走其他网卡。");
            var dataRoot = Path.GetFullPath(dataDirectory);
            var expectedCore = Path.Combine(dataRoot, "bin", "claude-safe-mihomo.exe");
            var corePath = Path.GetFullPath(settings.CorePath);
            if (!corePath.Equals(expectedCore, StringComparison.OrdinalIgnoreCase) || !File.Exists(corePath) ||
                settings.CoreSha256.Length != 64 || settings.CoreSha256.Any(c => !char.IsAsciiHexDigit(c)))
                throw new SafeOperationException("测速需要已核验的本地专用内核及 SHA-256。");
            using (var file = File.OpenRead(corePath))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(file, cancel).ConfigureAwait(false)).Equals(settings.CoreSha256, StringComparison.OrdinalIgnoreCase))
                    throw new SafeOperationException("测速内核与批准的 SHA-256 不符，已拒绝启动。");
            string parent = Path.Combine(dataRoot, "benchmarks"); Directory.CreateDirectory(parent);
            directory = Path.GetFullPath(Path.Combine(parent, "work-" + run.Id));
            if (!directory.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new SafeOperationException("无效的临时测速目录。");
            Directory.CreateDirectory(directory);
            var ports = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in run.NodeNames)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Server.ExclusiveAddressUse = true; listener.Start();
                reservations.Add(listener);
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (port is 7897 or CoreConfig.ProxyPort or CoreConfig.ControllerPort) throw new SafeOperationException("系统分配了保留端口，测速已停止。");
                ports.Add(name, port);
            }
            var configuration = BuildIsolatedConfiguration(settings, leaves, ports);
            string filePath = Path.Combine(directory, "config.yaml");
            await File.WriteAllTextAsync(filePath, CoreConfig.SerializeForCore(configuration), new UTF8Encoding(false), cancel).ConfigureAwait(false);
            job = new OwnedJob();
            using (var check = new Process { StartInfo = StartInfo(corePath, directory, "-t", "-d", directory, "-f", filePath) })
            {
                check.Start(); job.Assign(check);
                var stdout = DrainAsync(check.StandardOutput); var stderr = DrainAsync(check.StandardError);
                using var checkDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); checkDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                try { await check.WaitForExitAsync(checkDeadline.Token).ConfigureAwait(false); }
                catch { if (!check.HasExited) check.Kill(true); cancel.ThrowIfCancellationRequested(); throw new SafeOperationException("测速核心配置检查超时，当前使用节点未改变。"); }
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                if (check.ExitCode != 0) throw new SafeOperationException("测速核心拒绝候选配置，当前使用节点未改变。");
            }
            foreach (var listener in reservations) listener.Stop(); reservations.Clear();
            cancel.ThrowIfCancellationRequested();
            core = new Process { StartInfo = StartInfo(corePath, directory, "-d", directory, "-f", filePath) };
            core.Start(); job.Assign(core);
            core.OutputDataReceived += (_, _) => { }; core.ErrorDataReceived += (_, _) => { };
            core.BeginOutputReadLine(); core.BeginErrorReadLine();
            bool ready = false;
            for (int i = 0; i < 40; i++)
            {
                cancel.ThrowIfCancellationRequested();
                if (core.HasExited) break;
                if (BenchmarkPortOwnership.ExactlyOwnedBy(core.Id, ports.Values)) { ready = true; break; }
                await Task.Delay(200, cancel).ConfigureAwait(false);
            }
            if (!ready) throw new SafeOperationException("临时测速监听未通过进程归属检查；未连接生产代理。");
            lock (sync) { run.Status = "running"; run.Message = "使用独立回环测试通道下载 Desktop 公开资源，当前节点保持不变。"; }
            Report();
            using var parallel = new SemaphoreSlim(options.Concurrency);
            await Task.WhenAll(run.NodeNames.Select(async name =>
            {
                await parallel.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                    for (int round = 1; round <= options.Rounds; round++)
                    foreach (var target in run.Targets)
                    foreach (var protocol in protocols)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (core.HasExited || !BenchmarkPortOwnership.ExactlyOwnedBy(core.Id, ports.Values))
                            throw new SafeOperationException("临时测速内核或监听已退出；不会改用生产通道。");
                        var sample = await DesktopBenchmarkHttpProbe.ProbeAsync(name, ports[name], target, protocol, round, options.TimeoutSeconds, cancel).ConfigureAwait(false);
                        lock (sync) { run.Samples.Add(sample); run.Completed = run.Samples.Count; UpdateRanks(); }
                        Report();
                        cancel.ThrowIfCancellationRequested();
                    }
                }
                finally { parallel.Release(); }
            })).ConfigureAwait(false);
            lock (sync) { run.Status = "completed"; run.Message = "测速完成。全通过要求每个目标、协议和轮次都恰好有一个通过样本。"; }
        }
        catch (OperationCanceledException)
        {
            lock (sync) { run.Status = "cancelled"; run.Cancelled = true; run.Message = "测速已取消；未完成结果不能用于自动切换。"; }
        }
        catch (SafeOperationException e)
        {
            lock (sync) { run.Status = "error"; run.Message = e.Message; }
        }
        catch
        {
            lock (sync) { run.Status = "error"; run.Message = "测速未完成，临时核心已清理；未改变当前使用节点。"; }
        }
        finally
        {
            foreach (var listener in reservations) listener.Stop();
            if (core is not null)
            {
                try { if (!core.HasExited) { core.Kill(true); await core.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } } catch { }
                core.Dispose();
            }
            job?.Dispose();
            if (directory is not null) CleanupOwnedDirectory(dataDirectory, directory);
            lock (sync) { run.FinishedUtc = DateTimeOffset.UtcNow; if (protocols.Length > 0) UpdateRanks(); }
            Report();
        }
        lock (sync) return DesktopBenchmarkCopies.Clone(run);
    }

    public static JsonObject BuildIsolatedConfiguration(ManagerSettings settings, IReadOnlyList<JsonObject> nodes, IReadOnlyDictionary<string, int> ports)
    {
        if (ports.Count == 0 || ports.Values.Distinct().Count() != ports.Count || ports.Values.Any(p => p is < 1 or > 65535 or 7897 or CoreConfig.ProxyPort or CoreConfig.ControllerPort))
            throw new SafeOperationException("测速监听端口无效、重复或占用保留端口。");
        var approved = CoreConfig.Summaries(settings, nodes).Where(n => n.Approved && n.Eligible).Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        if (ports.Keys.Any(n => !approved.Contains(n))) throw new SafeOperationException("测速监听只能引用身份匹配的批准节点。");
        var selected = nodes.Where(n => ports.ContainsKey(CoreConfig.Identity(n).Name)).ToList();
        var result = CoreConfig.Build(settings, selected, "");
        result["external-controller"] = ""; result.Remove("secret"); result["proxy-groups"] = new JsonArray();
        result["log-level"] = "silent";
        result["listeners"] = new JsonArray(ports.Select((pair, i) => (JsonNode)new JsonObject
        {
            ["name"] = "desktop-test-" + i, ["type"] = "mixed", ["listen"] = "127.0.0.1", ["port"] = pair.Value,
            ["udp"] = false, ["proxy"] = pair.Key
        }).ToArray());
        return result;
    }

    private static ProcessStartInfo StartInfo(string executable, string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        char[] buffer = new char[4096];
        while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0) { }
    }
    private static void CleanupOwnedDirectory(string dataDirectory, string ownedDirectory)
    {
        try
        {
            var parent = Path.GetFullPath(Path.Combine(dataDirectory, "benchmarks")) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(ownedDirectory);
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("work-", StringComparison.Ordinal) ||
                (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return;
            // The absolute target has been checked against the owned benchmark workspace.
            Directory.Delete(target, recursive: true);
        }
        catch { /* An antivirus/file lock may delay removal. Never broaden the deletion target. */ }
    }
}

internal static class BenchmarkPortOwnership
{
    public static bool ExactlyOwnedBy(int pid, IEnumerable<int> expected)
    {
        int size = 0; _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (size < sizeof(int)) return false;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0) != 0) return false;
            int count = Marshal.ReadInt32(buffer), rowSize = Marshal.SizeOf<TcpRow>();
            var ports = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(IntPtr.Add(buffer, sizeof(int) + i * rowSize));
                if (row.ProcessId != pid) continue;
                if (row.State != 2 || row.LocalAddress != 0x0100007f) return false;
                ports.Add((int)(((row.LocalPort & 0xff) << 8) | ((row.LocalPort >> 8) & 0xff)));
            }
            return ports.SetEquals(expected);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct TcpRow { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, ProcessId; }
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
}
