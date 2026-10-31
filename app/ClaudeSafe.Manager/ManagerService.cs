using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSafe.Manager;

public sealed class ManagerService : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ManagerSettings settings;
    private List<JsonObject> nodes;
    private Process? core;
    private OwnedJob? job;
    private HttpClient? controller;
    private string selected = "REJECT";
    private string message = "专用内核未启动。启动后默认阻断，请手动选择批准节点。";
    private bool disposed;
    public string DataDirectory { get; }
    private string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    private string SourcePath => Path.Combine(DataDirectory, "source.json");
    private string WorkDirectory => Path.Combine(DataDirectory, "work");

    public ManagerService(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(SettingsPath), ManagerJson.Options) ?? throw new FormatException()
                : new ManagerSettings();
            CoreConfig.ValidatePins(settings.Pins);
            nodes = File.Exists(SourcePath) ? CoreConfig.ParseSource(File.ReadAllText(SourcePath)) : [];
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("本机数据无法读取。请检查 settings.json 和 source.json；程序不会覆盖损坏的配置。"); }
    }

    public static List<string> PhysicalInterfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
        .Where(n => !new[] { "tun", "tap", "wintun", "wireguard", "virtual", "hyper-v", "vmware", "veth", "mihomo", "clash" }
            .Any(part => (n.Name + " " + n.Description).Contains(part, StringComparison.OrdinalIgnoreCase)))
        .Where(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
        .Select(n => n.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private void RequireInterface()
    {
        if (!PhysicalInterfaces().Contains(settings.InterfaceName, StringComparer.Ordinal))
            throw new SafeOperationException("选定的真实网卡当前不可用；请重新选择。专用内核不会自动切换到其他接口。");
    }

    public async Task<ManagerSnapshot> SnapshotAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsRunning)
            {
                try { RequireInterface(); await VerifyRuntimeAsync(false).ConfigureAwait(false); }
                catch { await StopCoreAsync().ConfigureAwait(false); message = "连接控制或选定网卡不可用，已停止专用内核。请检查后重新启动。"; }
            }
            else if (core is not null) { await StopCoreAsync().ConfigureAwait(false); message = "专用内核已退出；当前通道不可用。"; }
            return new ManagerSnapshot { Running = IsRunning, ProcessId = IsRunning ? core!.Id : null, Selected = selected,
                InterfaceName = settings.InterfaceName, Message = message, Nodes = CoreConfig.Summaries(settings, nodes) };
        }
        finally { gate.Release(); }
    }

    private bool IsRunning => core is not null && !core.HasExited;
    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(ManagerService)); }

    public async Task StartAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsRunning) return;
            await StopCoreAsync().ConfigureAwait(false);
            RequireInterface();
            var corePath = Path.GetFullPath(settings.CorePath.Length == 0 ? Path.Combine(DataDirectory, "bin", "claude-safe-mihomo.exe") : settings.CorePath);
            if (!corePath.Equals(Path.Combine(DataDirectory, "bin", "claude-safe-mihomo.exe"), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(corePath) || settings.CoreSha256.Length != 64)
                throw new SafeOperationException("缺少已核验的专用内核。请按部署说明准备数据目录中的 bin/claude-safe-mihomo.exe 和 SHA-256。");
            using (var stream = File.OpenRead(corePath))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false)).Equals(settings.CoreSha256, StringComparison.OrdinalIgnoreCase))
                    throw new SafeOperationException("专用内核文件与批准的 SHA-256 不符，已拒绝启动。");
            EnsurePortFree(CoreConfig.ProxyPort); EnsurePortFree(CoreConfig.ControllerPort);
            Directory.CreateDirectory(WorkDirectory);
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var config = CoreConfig.Build(settings, nodes, secret);
            var configPath = Path.Combine(WorkDirectory, "config.yaml");
            AtomicWrite(configPath, CoreConfig.SerializeForCore(config));
            // -t parses the exact generated configuration. Its raw output stays in memory and is not shown or logged.
            using (var check = new Process { StartInfo = StartInfo(corePath, "-t", "-d", WorkDirectory, "-f", configPath) })
            {
                check.Start(); var stdout = check.StandardOutput.ReadToEndAsync(); var stderr = check.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await check.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                catch { if (!check.HasExited) check.Kill(true); throw new SafeOperationException("内核配置检查超时，未启动专用通道。"); }
                _ = await stdout.ConfigureAwait(false); _ = await stderr.ConfigureAwait(false);
                if (check.ExitCode != 0) throw new SafeOperationException("内核拒绝候选配置。请检查节点协议与当前内核版本；专用通道未启动。");
            }
            job = new OwnedJob();
            core = new Process { StartInfo = StartInfo(corePath, "-d", WorkDirectory, "-f", configPath), EnableRaisingEvents = true };
            core.OutputDataReceived += (_, _) => { }; core.ErrorDataReceived += (_, _) => { };
            core.Start(); job.Assign(core); core.BeginOutputReadLine(); core.BeginErrorReadLine();
            controller = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{CoreConfig.ControllerPort}/"), Timeout = TimeSpan.FromSeconds(3) };
            controller.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            bool ready = false;
            for (int i = 0; i < 40; i++)
            {
                if (!IsRunning) break;
                try { await VerifyRuntimeAsync(true).ConfigureAwait(false); ready = true; break; } catch { await Task.Delay(200).ConfigureAwait(false); }
            }
            if (!ready) throw new SafeOperationException("专用内核未通过启动核对，已停止。请检查端口占用及配置。");
            selected = "REJECT"; message = "专用内核已启动，当前为 REJECT。请选择批准节点后连接。";
        }
        catch (SafeOperationException) { await StopCoreAsync().ConfigureAwait(false); throw; }
        catch { await StopCoreAsync().ConfigureAwait(false); throw new SafeOperationException("专用内核启动失败；没有修改系统代理、TUN 或其他内核。"); }
        finally { gate.Release(); }
    }

    private static ProcessStartInfo StartInfo(string exe, params string[] arguments)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return info;
    }

    private static void EnsurePortFree(int port)
    {
        try { var listener = new TcpListener(IPAddress.Loopback, port); listener.Server.ExclusiveAddressUse = true; listener.Start(); listener.Stop(); }
        catch { throw new SafeOperationException($"专用端口 {port} 已被占用；请先完成旧监听迁移，程序不会结束其他进程。"); }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { ThrowIfDisposed(); await StopCoreAsync().ConfigureAwait(false); message = "专用内核已停止，受保护应用的专用通道不可用。"; }
        finally { gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        controller?.Dispose(); controller = null;
        if (core is not null)
        {
            try { if (!core.HasExited) { core.Kill(true); await core.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } } catch { }
            core.Dispose(); core = null;
        }
        job?.Dispose(); job = null; selected = "REJECT";
    }

    private async Task<JsonNode> ApiAsync(HttpMethod method, string path, JsonNode? value = null)
    {
        if (!IsRunning || controller is null || !PortOwnership.BothPortsOwnedBy(core!.Id)) throw new SafeOperationException("专用内核未运行或端口归属不符。");
        using var request = new HttpRequestMessage(method, path);
        if (value is not null) request.Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await controller.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new SafeOperationException("专用内核控制请求失败。");
        var bytes = await ReadLimitedAsync(response, 2 * 1024 * 1024).ConfigureAwait(false);
        return bytes.Length == 0 ? new JsonObject() : JsonNode.Parse(bytes) ?? throw new SafeOperationException("专用内核返回无效状态。");
    }

    private async Task VerifyRuntimeAsync(bool initial)
    {
        var proxies = await ApiAsync(HttpMethod.Get, "proxies").ConfigureAwait(false);
        var group = proxies["proxies"]?[CoreConfig.Group] ?? throw new FormatException();
        var expected = CoreConfig.Summaries(settings, nodes).Where(n => n.Approved && n.Eligible).Select(n => n.Name).Append("REJECT").Order(StringComparer.Ordinal);
        var all = group["all"]?.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal) ?? throw new FormatException();
        var current = group["now"]?.GetValue<string>() ?? throw new FormatException();
        if (!expected.SequenceEqual(all) || (initial && current != "REJECT") || !all.Contains(current)) throw new FormatException();
        var config = await ApiAsync(HttpMethod.Get, "configs").ConfigureAwait(false);
        if (config["tun"]?["enable"]?.GetValue<bool>() != false || config["mixed-port"]?.GetValue<int>() != 0 ||
            config["interface-name"]?.GetValue<string>() != settings.InterfaceName || config["mode"]?.GetValue<string>() != "rule") throw new FormatException();
        selected = current;
    }

    public async Task SelectAsync(string name)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); RequireInterface();
            if (name != "REJECT" && !CoreConfig.Summaries(settings, nodes).Any(n => n.Name == name && n.Approved && n.Eligible))
                throw new SafeOperationException("只能选择身份匹配的已批准节点。");
            try
            {
                await ApiAsync(HttpMethod.Put, "proxies/" + Uri.EscapeDataString(CoreConfig.Group), new JsonObject { ["name"] = name }).ConfigureAwait(false);
                await ApiAsync(HttpMethod.Delete, "connections").ConfigureAwait(false);
                await VerifyRuntimeAsync(false).ConfigureAwait(false);
                if (selected != name) throw new FormatException();
                message = name == "REJECT" ? "已切换到 REJECT，并关闭专用内核的已有连接。" : "已选择批准节点；请运行出口检查。";
            }
            catch { await StopCoreAsync().ConfigureAwait(false); throw new SafeOperationException("切换未通过核对，已停止专用内核以保持阻断。"); }
        }
        finally { gate.Release(); }
    }

    public async Task ImportFileAsync(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > CoreConfig.MaximumSourceBytes) throw new SafeOperationException("文件不存在或超过 16 MB。");
        string text;
        try { text = await File.ReadAllTextAsync(path).ConfigureAwait(false); } catch { throw new SafeOperationException("无法读取所选配置文件。"); }
        await ImportTextAsync(text).ConfigureAwait(false);
    }

    public async Task ImportUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new SafeOperationException("请输入没有用户名或片段的 HTTPS 订阅地址。");
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new SafeOperationException("订阅下载失败；不自动跟随重定向。可先下载为文件再导入。");
            var bytes = await ReadLimitedAsync(response, CoreConfig.MaximumSourceBytes, deadline.Token).ConfigureAwait(false);
            await ImportTextAsync(new UTF8Encoding(false, true).GetString(bytes)).ConfigureAwait(false);
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("订阅无法下载或格式无效。可将配置下载为 YAML/JSON 后导入；地址不会写入日志。"); }
    }

    private async Task ImportTextAsync(string text)
    {
        var candidate = CoreConfig.ParseSource(text);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); await StopCoreAsync().ConfigureAwait(false);
            var wrapper = new JsonObject { ["proxies"] = new JsonArray(candidate.Select(n => n.DeepClone()).ToArray()) };
            AtomicWrite(SourcePath, wrapper.ToJsonString(ManagerJson.Options)); nodes = candidate;
            message = "节点来源已导入，内核保持停止。只有批准身份完全匹配的节点可被选择。";
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("来源未能保存，专用内核保持停止。"); }
        finally { gate.Release(); }
    }

    public async Task ApproveAsync(string name)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var node = nodes.SingleOrDefault(n => CoreConfig.Identity(n).Name == name);
            if (node is null || CoreConfig.IneligibleReason(node).Length != 0 || name == settings.InterfaceName)
                throw new SafeOperationException("该节点缺失、身份不明确或含不支持的路由/证书设置。");
            await StopCoreAsync().ConfigureAwait(false);
            settings.Pins.RemoveAll(p => p.Name == name); settings.Pins.Add(CoreConfig.Identity(node)); SaveSettings();
            message = "该节点身份已批准。内核保持停止，下次启动后仍默认 REJECT。";
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("批准记录未能保存，专用内核保持停止。"); }
        finally { gate.Release(); }
    }

    public async Task RevokeAsync(string name)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { ThrowIfDisposed(); await StopCoreAsync().ConfigureAwait(false); settings.Pins.RemoveAll(p => p.Name == name); SaveSettings(); message = "批准已移除，内核保持停止。"; }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("记录未能保存，专用内核保持停止。"); }
        finally { gate.Release(); }
    }

    public async Task SetInterfaceAsync(string name)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!PhysicalInterfaces().Contains(name, StringComparer.Ordinal)) throw new SafeOperationException("请选择当前可用的真实出站网卡。");
            await StopCoreAsync().ConfigureAwait(false); settings.InterfaceName = name; SaveSettings();
            message = "真实出站网卡已保存，内核保持停止。";
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("网卡配置未能保存，专用内核保持停止。"); }
        finally { gate.Release(); }
    }

    public async Task<ProbeResult> ProbeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false); var timer = Stopwatch.StartNew();
        try
        {
            ThrowIfDisposed(); RequireInterface(); await VerifyRuntimeAsync(false).ConfigureAwait(false);
            if (selected == "REJECT") return new ProbeResult { Message = "当前为 REJECT，请先选择批准节点。" };
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = true, Proxy = new WebProxy($"http://127.0.0.1:{CoreConfig.ProxyPort}", false), AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(12) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var response = await http.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new FormatException();
            var text = Encoding.UTF8.GetString(await ReadLimitedAsync(response, 8192, deadline.Token).ConfigureAwait(false));
            var country = text.Split('\n').FirstOrDefault(s => s.StartsWith("loc=", StringComparison.Ordinal))?[4..].Trim() ?? "";
            if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper)) throw new FormatException();
            bool expected = string.IsNullOrEmpty(settings.ExpectedCountry) || country == settings.ExpectedCountry;
            return new ProbeResult { Success = expected, Country = country, ElapsedMs = timer.ElapsedMilliseconds,
                Message = expected ? "匿名出口检查通过。此结果不代表节点信誉或账号安全。" : "出口国家与预期不符；请停止使用该节点并检查。" };
        }
        catch (SafeOperationException) { throw; }
        catch { return new ProbeResult { Success = false, ElapsedMs = timer.ElapsedMilliseconds, Message = "匿名出口检查失败或超时；没有尝试直连回退。" }; }
        finally { gate.Release(); }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int maximum, CancellationToken cancellation = default)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new SafeOperationException("响应超过大小上限。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false); using var memory = new MemoryStream();
        byte[] buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
        { if (memory.Length + count > maximum) throw new SafeOperationException("响应超过大小上限。"); await memory.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false); }
        return memory.ToArray();
    }

    private void SaveSettings() => AtomicWrite(SettingsPath, JsonSerializer.Serialize(settings, ManagerJson.Options));
    private static void AtomicWrite(string path, string content)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, content, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose()
    {
        // IDisposable can be called on the WinForms thread while a refresh owns the gate.
        // Shutdown and every backend continuation must be independent of its message pump.
        Task.Run(async () =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed) return;
                disposed = true;
                await StopCoreAsync().ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }).GetAwaiter().GetResult();
    }
}
