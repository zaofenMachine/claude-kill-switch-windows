using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeSafe.Manager;

public sealed partial class ManagerService
{
    private readonly Func<HttpMessageHandler> subscriptionHandlerFactory;
    private SubscriptionCatalogState subscriptions = new();
    private int subscriptionOperations;
    private string subscriptionMessage = "可保存多个订阅；只有当前使用的订阅提供专用通道节点。";
    private string SubscriptionsPath => Path.Combine(DataDirectory, "subscriptions.json");

    private static HttpMessageHandler CreateSubscriptionHandler() => new SocketsHttpHandler
    {
        // Honor the OS proxy independently of the dedicated core's routing configuration.
        UseProxy = true, Proxy = null, DefaultProxyCredentials = null, Credentials = null,
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None
    };

    private void InitializeSubscriptions()
    {
        if (SubscriptionCatalog.Exists(SubscriptionsPath)) subscriptions = SubscriptionCatalog.Read(SubscriptionsPath);
        else
        {
            var legacyNodes = SubscriptionSourceReader.LoadActiveNodes(DataDirectory);
            subscriptions = new SubscriptionCatalogState();
            if (File.Exists(SourcePath))
            {
                var legacy = new StoredSubscription
                {
                    Name = "已导入的本地节点", Kind = "file", Nodes = legacyNodes,
                    LastUpdatedUtc = File.GetLastWriteTimeUtc(SourcePath)
                };
                subscriptions.Items.Add(legacy);
                subscriptions.ActiveId = legacy.Id;
            }
            WriteSubscriptions(subscriptions);
        }
        var active = subscriptions.Items.SingleOrDefault(item => item.Id == subscriptions.ActiveId);
        nodes = active is null ? [] : SubscriptionCatalog.CloneNodes(active.Nodes);
    }

    private void WriteSubscriptions(SubscriptionCatalogState candidate) =>
        AtomicWrite(SubscriptionsPath, SubscriptionCatalog.Serialize(candidate));

    private static void RequireAvailableName(SubscriptionCatalogState catalog, string name, string exceptId = "")
    {
        if (catalog.Items.Any(item => item.Id != exceptId && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new SafeOperationException("已存在同名订阅，请换一个名称。");
    }

    private StoredSubscription RequireSubscription(string id) =>
        subscriptions.Items.SingleOrDefault(item => item.Id == id)
        ?? throw new SafeOperationException("该订阅已不存在，请刷新列表。");

    private static void RequireCapacity(SubscriptionCatalogState catalog)
    {
        if (catalog.Items.Count >= SubscriptionCatalog.MaximumProfiles)
            throw new SafeOperationException("最多保存 32 份订阅或本地配置；请先移除不再需要的条目。");
    }

    public async Task<SubscriptionSnapshot> SubscriptionsAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return new SubscriptionSnapshot
            {
                ActiveId = subscriptions.ActiveId, Busy = Volatile.Read(ref subscriptionOperations) > 0,
                Message = subscriptionMessage,
                Items = subscriptions.Items.Select(item => new SubscriptionSummary
                {
                    Id = item.Id, Name = item.Name, Kind = item.Kind, Active = item.Id == subscriptions.ActiveId,
                    MaskedUrl = SubscriptionCatalog.MaskedUrl(item), NodeCount = item.Nodes.Count,
                    LastUpdatedUtc = item.LastUpdatedUtc, LastError = SubscriptionCatalog.ErrorMessage(item.LastErrorCode)
                }).ToList()
            };
        }
        finally { gate.Release(); }
    }

    public async Task AddSubscriptionAsync(string name, string url)
    {
        name = SubscriptionCatalog.ValidateName(name);
        _ = SubscriptionCatalog.ValidateUrl(url);
        Interlocked.Increment(ref subscriptionOperations);
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { ThrowIfDisposed(); RequireCapacity(subscriptions); RequireAvailableName(subscriptions, name); }
            finally { gate.Release(); }
            var leaves = await FetchSubscriptionAsync(url).ConfigureAwait(false);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed(); RequireCapacity(subscriptions); RequireAvailableName(subscriptions, name);
                var candidate = SubscriptionCatalog.Clone(subscriptions);
                candidate.Items.Add(new StoredSubscription
                { Name = name, Kind = "https", Url = url, Nodes = leaves, LastUpdatedUtc = DateTimeOffset.UtcNow });
                WriteSubscriptions(candidate); subscriptions = candidate;
                subscriptionMessage = "订阅已保存。选择“使用此订阅”后才会提供专用通道节点；新节点仍须手动批准。";
            }
            finally { gate.Release(); }
        }
        catch (SubscriptionDownloadException e) { throw new SafeOperationException(SubscriptionCatalog.ErrorMessage(e.Code)); }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("订阅未能保存。原有节点、批准记录和专用通道保持不变。"); }
        finally { Interlocked.Decrement(ref subscriptionOperations); }
    }

    // Compatibility entry point: HTTPS imports now create an inactive saved subscription.
    public async Task ImportUrlAsync(string url)
    {
        _ = SubscriptionCatalog.ValidateUrl(url);
        string name;
        await gate.WaitAsync().ConfigureAwait(false);
        try { ThrowIfDisposed(); name = UniqueSubscriptionName("HTTPS 订阅"); }
        finally { gate.Release(); }
        await AddSubscriptionAsync(name, url).ConfigureAwait(false);
    }

    public async Task ImportFileAsync(string path)
    {
        Interlocked.Increment(ref subscriptionOperations);
        try
        {
            var leaves = await ReadLocalSubscriptionAsync(path).ConfigureAwait(false);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed(); RequireCapacity(subscriptions);
                var candidate = SubscriptionCatalog.Clone(subscriptions);
                var stem = Path.GetFileNameWithoutExtension(path);
                var name = UniqueSubscriptionName(string.IsNullOrWhiteSpace(stem) ? "本地配置" : stem);
                var item = new StoredSubscription { Name = name, Kind = "file", Nodes = leaves, LastUpdatedUtc = DateTimeOffset.UtcNow };
                candidate.Items.Add(item); candidate.ActiveId = item.Id;
                var serialized = SubscriptionCatalog.Serialize(candidate);
                var activeNodes = SubscriptionCatalog.CloneNodes(leaves);
                configurationRevision++;
                await StopCoreAsync().ConfigureAwait(false);
                AtomicWrite(SubscriptionsPath, serialized); subscriptions = candidate; nodes = activeNodes;
                message = subscriptionMessage = "本地配置已保存并设为当前来源，专用内核保持停止。请审阅节点并手动启动、选择批准节点。";
            }
            finally { gate.Release(); }
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("本地配置未能导入；原有来源缓存未被覆盖。若已停止内核，请手动重新启动。"); }
        finally { Interlocked.Decrement(ref subscriptionOperations); }
    }

    private string UniqueSubscriptionName(string stem)
    {
        stem = new string(stem.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (stem.Length == 0) stem = "本地配置";
        if (stem.Length > 68) stem = stem[..68];
        string name = stem;
        for (int i = 2; subscriptions.Items.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{stem} ({i})";
        return name;
    }

    private async Task<List<JsonObject>> ReadLocalSubscriptionAsync(string path)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(benchmarkLifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            if (stream.Length > CoreConfig.MaximumSourceBytes) throw new SafeOperationException("本地配置超过 16 MB 上限。");
            using var memory = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + count > CoreConfig.MaximumSourceBytes) throw new SafeOperationException("本地配置超过 16 MB 上限。");
                await memory.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
            }
            return CoreConfig.ParseSource(new UTF8Encoding(false, true).GetString(memory.ToArray()));
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("无法读取所选文件，或文件不是有效的 UTF-8 Mihomo YAML/JSON 配置。"); }
    }

    public async Task UpdateSubscriptionAsync(string id)
    {
        Interlocked.Increment(ref subscriptionOperations);
        SubscriptionTicket? ticket = null;
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed(); var item = RequireSubscription(id);
                if (item.Kind != "https") throw new SafeOperationException("本地配置是文件快照；请重新导入文件以更新。");
                ticket = new SubscriptionTicket(item.Id, item.Url, item.Revision);
            }
            finally { gate.Release(); }
            var leaves = await FetchSubscriptionAsync(ticket.Url).ConfigureAwait(false);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed(); var current = MatchingTicket(ticket);
                if (current is null) throw new SafeOperationException("更新期间订阅已被替换、移除或另一次更新改动，本次结果未应用。");
                var candidate = SubscriptionCatalog.Clone(subscriptions);
                var updated = candidate.Items.Single(item => item.Id == id);
                updated.Nodes = leaves; updated.LastUpdatedUtc = DateTimeOffset.UtcNow;
                updated.LastErrorCode = ""; updated.Revision = Guid.NewGuid().ToString("N");
                bool activeChanged = subscriptions.ActiveId == id && !SubscriptionCatalog.SameNodes(nodes, leaves);
                var serialized = SubscriptionCatalog.Serialize(candidate);
                var activeNodes = activeChanged ? SubscriptionCatalog.CloneNodes(leaves) : null;
                if (activeChanged) { configurationRevision++; await StopCoreAsync().ConfigureAwait(false); }
                AtomicWrite(SubscriptionsPath, serialized); subscriptions = candidate;
                if (activeChanged)
                {
                    nodes = activeNodes!;
                    message = subscriptionMessage = "当前订阅的节点内容已变化，专用内核已停止。批准记录仍按原身份核对，请审阅后手动启动、选择节点。";
                }
                else subscriptionMessage = subscriptions.ActiveId == id
                    ? "当前订阅更新成功，节点内容未变，专用通道未中断。"
                    : "订阅更新成功；当前正在使用的来源和专用通道未改变。";
            }
            finally { gate.Release(); }
        }
        catch (SubscriptionDownloadException e)
        {
            if (ticket is not null) await RecordSubscriptionFailureAsync(ticket, e.Code).ConfigureAwait(false);
            throw new SafeOperationException(SubscriptionCatalog.ErrorMessage(e.Code));
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("更新结果未能保存，原缓存仍保留。若当前内核已停止，请审阅后手动重新启动。"); }
        finally { Interlocked.Decrement(ref subscriptionOperations); }
    }

    public async Task UpdateAllSubscriptionsAsync()
    {
        Interlocked.Increment(ref subscriptionOperations);
        try
        {
            string[] ids;
            await gate.WaitAsync().ConfigureAwait(false);
            try { ThrowIfDisposed(); ids = subscriptions.Items.Where(item => item.Kind == "https").Select(item => item.Id).ToArray(); }
            finally { gate.Release(); }
            if (ids.Length == 0) throw new SafeOperationException("当前没有可更新的 HTTPS 订阅。");
            int failed = 0;
            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = benchmarkLifetime.Token }, async (id, _) =>
            {
                try { await UpdateSubscriptionAsync(id).ConfigureAwait(false); }
                catch (SafeOperationException) { Interlocked.Increment(ref failed); }
            }).ConfigureAwait(false);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                subscriptionMessage = failed == 0 ? "全部 HTTPS 订阅已更新；本地文件快照未更改。"
                    : "部分订阅未能更新；成功项已保存，失败项保留上次有效节点。请查看各项结果。";
            }
            finally { gate.Release(); }
            if (failed > 0) throw new SafeOperationException("部分订阅未能更新；成功项已保存，失败项保留上次有效节点。请查看各项结果。");
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("订阅批量更新已中止，未完成的条目仍保留原缓存。"); }
        finally { Interlocked.Decrement(ref subscriptionOperations); }
    }

    public async Task RenameSubscriptionAsync(string id, string name)
    {
        name = SubscriptionCatalog.ValidateName(name);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); _ = RequireSubscription(id); RequireAvailableName(subscriptions, name, id);
            var candidate = SubscriptionCatalog.Clone(subscriptions);
            candidate.Items.Single(item => item.Id == id).Name = name;
            WriteSubscriptions(candidate); subscriptions = candidate;
            subscriptionMessage = "订阅名称已保存，当前节点与连接未改变。";
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("订阅名称无法保存；原名称已保留。"); }
        finally { gate.Release(); }
    }

    public async Task SetSubscriptionUrlAsync(string id, string url)
    {
        _ = SubscriptionCatalog.ValidateUrl(url);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); var item = RequireSubscription(id);
            if (item.Kind != "https") throw new SafeOperationException("本地配置不能设置订阅地址；请新增 HTTPS 订阅。");
            if (item.Url == url) return;
            var candidate = SubscriptionCatalog.Clone(subscriptions);
            var replacement = candidate.Items.Single(profile => profile.Id == id);
            replacement.Url = url; replacement.LastErrorCode = ""; replacement.Revision = Guid.NewGuid().ToString("N");
            WriteSubscriptions(candidate); subscriptions = candidate;
            subscriptionMessage = "订阅地址已替换，原缓存暂时保留。请执行更新以下载新地址的节点。";
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("新订阅地址无法保存；原地址和节点缓存已保留。"); }
        finally { gate.Release(); }
    }

    public async Task UseSubscriptionAsync(string id)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); var item = RequireSubscription(id);
            if (subscriptions.ActiveId == id) { subscriptionMessage = "此订阅已是当前来源。"; return; }
            var candidate = SubscriptionCatalog.Clone(subscriptions); candidate.ActiveId = id;
            var serialized = SubscriptionCatalog.Serialize(candidate);
            var activeNodes = SubscriptionCatalog.CloneNodes(item.Nodes);
            configurationRevision++;
            await StopCoreAsync().ConfigureAwait(false);
            AtomicWrite(SubscriptionsPath, serialized); subscriptions = candidate; nodes = activeNodes;
            message = subscriptionMessage = "已切换当前订阅，专用内核保持停止。请审阅批准状态后手动启动、选择节点。";
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("来源切换未能保存；专用内核保持停止，原来源缓存仍保留。"); }
        finally { gate.Release(); }
    }

    public async Task RemoveSubscriptionAsync(string id)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); _ = RequireSubscription(id);
            bool wasActive = subscriptions.ActiveId == id;
            var candidate = SubscriptionCatalog.Clone(subscriptions);
            candidate.Items.RemoveAll(item => item.Id == id);
            if (wasActive) candidate.ActiveId = "";
            var serialized = SubscriptionCatalog.Serialize(candidate);
            if (wasActive)
            {
                configurationRevision++;
                await StopCoreAsync().ConfigureAwait(false);
            }
            AtomicWrite(SubscriptionsPath, serialized); subscriptions = candidate;
            if (wasActive)
            {
                nodes = [];
                message = subscriptionMessage = "当前来源已移除，专用内核保持停止。请选择其他来源后手动启动；批准记录已保留。";
            }
            else subscriptionMessage = "订阅已移除；当前来源和专用通道未改变。";
        }
        catch (SafeOperationException) { throw; }
        catch { throw new SafeOperationException("订阅未能移除；原缓存已保留。若当前内核已停止，请手动重新启动。"); }
        finally { gate.Release(); }
    }

    private sealed record SubscriptionTicket(string Id, string Url, string Revision);
    private sealed class SubscriptionDownloadException(string code) : Exception { public string Code { get; } = code; }

    private StoredSubscription? MatchingTicket(SubscriptionTicket ticket) => subscriptions.Items.SingleOrDefault(item =>
        item.Id == ticket.Id && item.Url == ticket.Url && item.Revision == ticket.Revision);

    private async Task RecordSubscriptionFailureAsync(SubscriptionTicket ticket, string code)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || MatchingTicket(ticket) is null) return;
            var candidate = SubscriptionCatalog.Clone(subscriptions);
            candidate.Items.Single(item => item.Id == ticket.Id).LastErrorCode = code;
            WriteSubscriptions(candidate); subscriptions = candidate;
            subscriptionMessage = SubscriptionCatalog.ErrorMessage(code);
        }
        catch { /* A status-write failure must not overwrite last-good state or reveal a URL. */ }
        finally { gate.Release(); }
    }

    private async Task<List<JsonObject>> FetchSubscriptionAsync(string url)
    {
        var uri = SubscriptionCatalog.ValidateUrl(url);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(benchmarkLifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var http = new HttpClient(subscriptionHandlerFactory(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Clash.Meta ClaudeSafe-Manager/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/yaml"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new SubscriptionDownloadException("http");
            if (response.Content.Headers.ContentLength > CoreConfig.MaximumSourceBytes) throw new SubscriptionDownloadException("large");
            byte[] bytes;
            try { bytes = await ReadLimitedAsync(response, CoreConfig.MaximumSourceBytes, deadline.Token).ConfigureAwait(false); }
            catch (SafeOperationException) { throw new SubscriptionDownloadException("large"); }
            try { return CoreConfig.ParseSource(new UTF8Encoding(false, true).GetString(bytes)); }
            catch { throw new SubscriptionDownloadException("format"); }
        }
        catch (SubscriptionDownloadException) { throw; }
        catch { throw new SubscriptionDownloadException("download"); }
    }
}
