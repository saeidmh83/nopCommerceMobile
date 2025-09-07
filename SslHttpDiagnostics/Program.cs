using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SslHttpDiagnostics;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            Console.WriteLine("Usage: SslHttpDiagnostics <url> [--host invoice.royaljeans.ir] [--timeout 20]");
            Console.WriteLine("Example: SslHttpDiagnostics https://invoice.royaljeans.ir/SaleOrder/Index/abc --host invoice.royaljeans.ir");
            return 2;
        }

        var url = args[0];
        string? hostHeader = null;
        int timeoutSec = 30;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--host" && i + 1 < args.Length) hostHeader = args[++i];
            else if (args[i] == "--timeout" && i + 1 < args.Length && int.TryParse(args[++i], out var t)) timeoutSec = Math.Max(5, t);
        }

        var uri = new Uri(url);
        var hostname = hostHeader ?? uri.Host;

        var report = new DiagnosticReport(hostname, uri);

        await DnsTests.RunAsync(report);
        await TcpTlsTests.RunAsync(report);
        await HttpTests.RunAsync(report, timeoutSec);

        ReportPrinter.Print(report);

        return report.HasErrors ? 1 : 0;
    }
}

internal sealed class DiagnosticReport
{
    public DiagnosticReport(string host, Uri target)
    {
        Host = host;
        Target = target;
    }

    public string Host { get; }
    public Uri Target { get; }

    public List<string> Infos { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();

    public bool HasErrors => Errors.Count > 0;

    public void Info(string message) => Infos.Add(message);
    public void Warn(string message) => Warnings.Add(message);
    public void Error(string message) => Errors.Add(message);
}

internal static class DnsTests
{
    public static async Task RunAsync(DiagnosticReport r)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(r.Host);
            var v4 = entry.AddressList.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            var v6 = entry.AddressList.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToArray();
            r.Info($"DNS: {r.Host} → A: [{string.Join(", ", v4.Select(x => x.ToString()))}] AAAA: [{string.Join(", ", v6.Select(x => x.ToString()))}]");
            if (v4.Length == 0 && v6.Length == 0) r.Error("DNS: No A/AAAA records resolved.");
        }
        catch (Exception ex)
        {
            r.Error($"DNS: Resolution failed: {ex.Message}");
        }
    }
}

internal static class TcpTlsTests
{
    public static async Task RunAsync(DiagnosticReport r)
    {
        await TestTlsVersionAsync(r, SslProtocols.Tls12, alpn: "h2");
        await TestTlsVersionAsync(r, SslProtocols.Tls12, alpn: "http/1.1");
        await TestTlsVersionAsync(r, SslProtocols.Tls13, alpn: "h2");
        await TestTlsVersionAsync(r, SslProtocols.Tls13, alpn: "http/1.1");
    }

    private static async Task TestTlsVersionAsync(DiagnosticReport r, SslProtocols protocol, string alpn)
    {
        using var tcp = new TcpClient();
        tcp.ReceiveTimeout = 10000;
        tcp.SendTimeout = 10000;

        try
        {
            await tcp.ConnectAsync(r.Host, 443);
            using var net = tcp.GetStream();
            using var ssl = new SslStream(net, leaveInnerStreamOpen: false, (sender, cert, chain, errors) => true, userCertificateSelectionCallback: null);
            var opts = new SslClientAuthenticationOptions
            {
                TargetHost = r.Host,
                EnabledSslProtocols = protocol,
                CertificateRevocationCheckMode = X509RevocationMode.Online,
                ApplicationProtocols = new List<SslApplicationProtocol> { new(alpn) }
            };

            var sw = Stopwatch.StartNew();
            await ssl.AuthenticateAsClientAsync(opts);
            sw.Stop();

            var alpnTxt = ssl.NegotiatedApplicationProtocol.Protocol switch
            {
                var p when p == SslApplicationProtocol.Http2.Protocol => "h2",
                var p when p == SslApplicationProtocol.Http11.Protocol => "http/1.1",
                { Length: > 0 } p => p,
                _ => "(none)"
            };

            r.Info($"TLS {(protocol == SslProtocols.Tls13 ? "1.3" : "1.2")} + ALPN={alpn}: OK in {sw.ElapsedMilliseconds} ms; negotiated ALPN={alpnTxt}; cipher={ssl.NegotiatedCipherSuite}");

            // Certificate details
            var cert = new X509Certificate2(ssl.RemoteCertificate!);
            r.Info($"Cert: CN={cert.GetNameInfo(X509NameType.DnsName, false)}, Issuer={cert.Issuer}, NotAfter={cert.NotAfter:u}");

            using var chain = new X509Chain
            {
                ChainPolicy =
                {
                    RevocationMode = X509RevocationMode.Online,
                    RevocationFlag = X509RevocationFlag.ExcludeRoot,
                    VerificationFlags = X509VerificationFlags.NoFlag,
                    UrlRetrievalTimeout = TimeSpan.FromSeconds(10)
                }
            };
            var ok = chain.Build(cert);
            if (!ok)
            {
                var issues = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
                r.Warn($"Certificate chain not fully validated: {issues}");
            }

            // Quick HTTP HEAD over raw TLS to test HTTP/2 HEAD handling is omitted here; done in HttpTests
        }
        catch (Exception ex)
        {
            var protoName = protocol == SslProtocols.Tls13 ? "TLS1.3" : "TLS1.2";
            r.Warn($"TLS {protoName} + ALPN={alpn} failed: {ex.Message}");
        }
    }
}

internal static class HttpTests
{
    public static async Task RunAsync(DiagnosticReport r, int timeoutSec)
    {
        await TestHttpVersionAsync(r, timeoutSec, http2: false);
        await TestHttpVersionAsync(r, timeoutSec, http2: true);
    }

    private static async Task TestHttpVersionAsync(DiagnosticReport r, int timeoutSec, bool http2)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(timeoutSec),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = http2 ? new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 } : new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                CertificateRevocationCheckMode = X509RevocationMode.Online
            }
        };

        var version = http2 ? HttpVersion.Version20 : HttpVersion.Version11;
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSec)
        };
        client.DefaultRequestVersion = version;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;

        async Task<HttpResponseMessage?> TryAsync(Func<Task<HttpResponseMessage>> op, string name)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var resp = await op();
                sw.Stop();
                r.Info($"HTTP{(http2 ? "/2" : "/1.1")} {name}: {(int)resp.StatusCode} {resp.ReasonPhrase} in {sw.ElapsedMilliseconds} ms");
                return resp;
            }
            catch (Exception ex)
            {
                r.Warn($"HTTP{(http2 ? "/2" : "/1.1")} {name} failed: {ex.Message}");
                return null;
            }
        }

        // 1) HEAD to root
        var headRoot = await TryAsync(() => client.SendAsync(new HttpRequestMessage(HttpMethod.Head, new Uri($"https://{r.Host}/"))), "HEAD /");
        if (headRoot is { } hr)
        {
            AnalyzeHeaders(r, hr.Headers, hr.Content?.Headers, http2);
        }

        // 2) GET to root following redirects
        await FollowRedirectsAsync(r, client, new Uri($"https://{r.Host}/"), http2);

        // 3) HEAD to target
        var headTarget = await TryAsync(() => client.SendAsync(new HttpRequestMessage(HttpMethod.Head, r.Target)), "HEAD target");
        if (headTarget is { } ht)
        {
            AnalyzeHeaders(r, ht.Headers, ht.Content?.Headers, http2);
        }

        // 4) GET first 1 byte (Range)
        var rangeReq = new HttpRequestMessage(HttpMethod.Get, r.Target);
        rangeReq.Headers.Range = new RangeHeaderValue(0, 1);
        var rangeResp = await TryAsync(() => client.SendAsync(rangeReq), "GET Range 0-1");
        if (rangeResp is { } rr)
        {
            if (rr.StatusCode != HttpStatusCode.PartialContent)
            {
                r.Warn($"Range requests not supported properly (expected 206). Status={(int)rr.StatusCode}");
            }
            AnalyzeHeaders(r, rr.Headers, rr.Content?.Headers, http2);
        }

        // 5) Full GET with small timeout to detect stalls
        var getReq = new HttpRequestMessage(HttpMethod.Get, r.Target);
        var swGet = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            var full = await client.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            using var stream = await full.Content.ReadAsStreamAsync(cts.Token);
            var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
            int total = 0, reads = 0;
            while (reads < 10)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, 32 * 1024), cts.Token);
                if (read == 0) break;
                total += read;
                reads++;
            }
            ArrayPool<byte>.Shared.Return(buffer);
            swGet.Stop();
            r.Info($"HTTP{(http2 ? "/2" : "/1.1")} GET target streamed {total} bytes in {swGet.ElapsedMilliseconds} ms");
            if (total == 0)
            {
                r.Warn("GET target returned headers but no body data within time window (possible stall)");
            }
        }
        catch (Exception ex)
        {
            swGet.Stop();
            r.Warn($"HTTP{(http2 ? "/2" : "/1.1")} GET target stalled/failed after {swGet.ElapsedMilliseconds} ms: {ex.Message}");
        }
    }

    private static void AnalyzeHeaders(DiagnosticReport r, HttpResponseHeaders headers, HttpContentHeaders? contentHeaders, bool http2)
    {
        if (contentHeaders != null)
        {
            if (contentHeaders.ContentType is { } ct)
            {
                r.Info($"Content-Type: {ct}");
                if (ct.MediaType?.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) == true == false)
                {
                    r.Warn("Expected application/pdf for target content");
                }
            }
            if (!contentHeaders.Contains("Accept-Ranges"))
            {
                r.Warn("Missing Accept-Ranges: bytes (range requests help PDF rendering/resume)");
            }
        }

        if (headers.Location != null && !headers.Location.IsAbsoluteUri)
        {
            r.Warn($"Relative Location header detected: {headers.Location}. Prefer absolute URLs over HTTPS.");
        }

        if (http2 && headers.TryGetValues("Alt-Svc", out var altSvc))
        {
            r.Info($"Alt-Svc: {string.Join("; ", altSvc)}");
        }
    }

    private static async Task FollowRedirectsAsync(DiagnosticReport r, HttpClient client, Uri start, bool http2)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = start;
        for (int i = 0; i < 10; i++)
        {
            if (!visited.Add(current.ToString()))
            {
                r.Error($"Redirect loop detected at {current}");
                return;
            }
            HttpResponseMessage resp;
            try
            {
                resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, current));
            }
            catch (Exception ex)
            {
                r.Warn($"HTTP{(http2 ? "/2" : "/1.1")} HEAD {current} failed: {ex.Message}");
                return;
            }

            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
            {
                var next = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location : new Uri(current, resp.Headers.Location);
                r.Info($"Redirect: {(int)resp.StatusCode} → {next}");
                current = next;
                continue;
            }
            r.Info($"Final after redirects: {(int)resp.StatusCode} {resp.ReasonPhrase} at {current}");
            return;
        }
        r.Error("Too many redirects (10+)");
    }
}

internal static class ReportPrinter
{
    public static void Print(DiagnosticReport r)
    {
        Console.WriteLine("==== SSL/HTTP Diagnostics ====");
        Console.WriteLine($"Target: {r.Target}");
        Console.WriteLine($"Host:   {r.Host}");
        Console.WriteLine();

        foreach (var i in r.Infos) Console.WriteLine($"INFO: {i}");
        foreach (var w in r.Warnings) Console.WriteLine($"WARN: {w}");
        foreach (var e in r.Errors) Console.WriteLine($"ERROR: {e}");

        Console.WriteLine();
        Console.WriteLine("Suggestions:");
        var sb = new StringBuilder();
        // Heuristics
        if (r.Warnings.Any(x => x.Contains("TLS 1.3", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("- Ensure TLS 1.3 is enabled end-to-end or fall back cleanly to TLS 1.2.");
        if (r.Warnings.Any(x => x.Contains("ALPN=h2", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("- If HTTP/2 negotiation fails, disable HTTP/2 for the binding or update http.sys.");
        if (r.Warnings.Any(x => x.Contains("Range requests not supported", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("- Add 'Accept-Ranges: bytes' and support 206 for PDFs.");
        if (r.Errors.Any(x => x.Contains("DNS", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("- Fix DNS A/AAAA records or resolver issues.");
        if (r.Warnings.Any(x => x.Contains("Relative Location header", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("- Use absolute HTTPS URLs in redirects to avoid scheme confusion.");
        if (r.Warnings.Any(x => x.Contains("chain not fully validated", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("- Install intermediate CA (full chain) on the server/IIS.");
        if (sb.Length == 0) sb.AppendLine("- No obvious misconfiguration detected. Check network/firewall or app-level timeouts.");

        Console.WriteLine(sb.ToString());
    }
}

