using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace ClaudeSafe.Manager;

public static class DesktopBenchmarkHttpProbe
{
    public const int MaximumBodyBytes = 8 * 1024 * 1024;

    public static async Task<BenchmarkSample> ProbeAsync(string name, int port, BenchmarkTarget target, string protocol, int round,
        int timeoutSeconds, CancellationToken cancellation = default)
    {
        var uri = DesktopBenchmarkTargets.Validate(target);
        if (port is < 1 or > 65535 || protocol is not ("http/1.1" or "h2") || round is < 1 or > 10 || timeoutSeconds is < 3 or > 60)
            throw new SafeOperationException("Desktop 测速请求参数无效。");
        var sample = new BenchmarkSample { Name = name, TargetId = target.Id, Protocol = protocol, Round = round };
        var timer = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var handler = CreateHandler(port);
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Version = protocol == "h2" ? HttpVersion.Version20 : HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.UserAgent.ParseAdd("ClaudeSafe-Desktop-Diagnostic/2.0");
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            sample.TtfbMs = timer.Elapsed.TotalMilliseconds;
            sample.Http = (int)response.StatusCode;
            sample.ActualProtocol = response.Version == HttpVersion.Version20 ? "h2" : response.Version == HttpVersion.Version11 ? "http/1.1" : "HTTP/" + response.Version;
            // No custom certificate callback, client certificate, cookie jar or credentials are configured.
            // Receiving HTTPS headers therefore means the runtime's normal certificate verification succeeded.
            sample.TlsVerified = true;
            sample.ContentType = response.Content.Headers.ContentType?.ToString() ?? "";
            sample.ExpectedBytes = response.Content.Headers.ContentLength;
            if (sample.ActualProtocol != protocol)
            {
                sample.Status = "protocol_mismatch"; sample.Error = "服务器未使用所要求的 HTTP 协议。"; return sample;
            }
            if (sample.ExpectedBytes > MaximumBodyBytes)
            {
                sample.Status = "response_too_large"; sample.Error = "响应超过 8 MiB 上限。"; return sample;
            }
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            byte[] buffer = new byte[32 * 1024]; int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                sample.Bytes += count;
                if (sample.Bytes > MaximumBodyBytes)
                {
                    sample.Status = "response_too_large"; sample.Error = "响应超过 8 MiB 上限。"; return sample;
                }
            }
            sample.Complete = sample.ExpectedBytes is null || sample.Bytes == sample.ExpectedBytes;
            bool identityEncoding = response.Content.Headers.ContentEncoding.All(e => e.Equals("identity", StringComparison.OrdinalIgnoreCase));
            sample.ResourceValid = sample.Complete && sample.Http is >= 200 and < 300 && sample.Bytes > 0 && identityEncoding &&
                DesktopBenchmarkRanker.MimeMatches(target.ExpectedType, sample.ContentType);
            sample.Status = Classify(sample, target);
            sample.Error = sample.Status switch
            {
                "incomplete" => "实际接收字节数与响应长度不符。",
                "target_stale" => "资源已失效，请维护测速目标；这不是节点故障的充分证据。",
                "content_mismatch" => identityEncoding ? "资源内容类型或非空要求不符。" : "资源响应未遵守 identity 编码要求。",
                "restricted" => "匿名入口拒绝或要求认证。",
                "rate_limited" => "请求受到频率限制。",
                "http_error" => "服务返回未通过的 HTTP 状态。",
                "redirect" when target.Kind == "resource" => "资源被重定向；未跟随重定向。",
                _ => ""
            };
        }
        catch (OperationCanceledException)
        {
            sample.Status = cancellation.IsCancellationRequested ? "cancelled" : "timeout";
            sample.Error = cancellation.IsCancellationRequested ? "已取消。" : "请求达到整体超时上限。";
        }
        catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.VersionNegotiationError)
        { sample.Status = "protocol_mismatch"; sample.Error = "指定 HTTP 协议未能协商。"; }
        catch (HttpRequestException e) when (e.InnerException is AuthenticationException || e.HttpRequestError == HttpRequestError.SecureConnectionError)
        { sample.Status = "tls_error"; sample.Error = "TLS 连接或证书验证失败。"; }
        catch (Exception) when (cancellation.IsCancellationRequested)
        { sample.Status = "cancelled"; sample.Error = "已取消。"; }
        catch
        {
            sample.Status = sample.Http == 0 ? "network_error" : "incomplete";
            sample.Error = sample.Http == 0 ? "代理或网络请求失败；未尝试直连回退。" : "响应传输中断，未取得完整资源。";
        }
        finally { sample.TotalMs = timer.Elapsed.TotalMilliseconds; }
        return sample;
    }

    // The ConnectCallback additionally rejects any future handler behavior that attempts
    // to open the origin directly instead of the intended loopback proxy.
    public static SocketsHttpHandler CreateHandler(int port)
    {
        if (port is < 1 or > 65535) throw new SafeOperationException("无效的测试代理端口。");
        return new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy($"http://127.0.0.1:{port}", false),
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = async (context, token) =>
            {
                if (context.DnsEndPoint.Host != "127.0.0.1" || context.DnsEndPoint.Port != port)
                    throw new SafeOperationException("测试连接未指向指定回环代理，已拒绝。例外不会改为直连。");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
    }

    public static string Classify(BenchmarkSample sample, BenchmarkTarget target)
    {
        if (!sample.Complete) return "incomplete";
        if (target.Kind == "resource" && sample.Http is 404 or 410) return "target_stale";
        if (sample.Http is >= 200 and < 300)
            return target.Kind == "resource" && !sample.ResourceValid ? "content_mismatch" : "ok";
        if (sample.Http is >= 300 and < 400) return "redirect";
        if (sample.Http == 404 && target.Url == "https://api.anthropic.com/") return "api_root_404";
        if (sample.Http == 429) return "rate_limited";
        if (sample.Http is 401 or 403) return "restricted";
        return "http_error";
    }
}
