using System.Net;
using System.Net.Http;
using System.Text;
using StarRailUnlocker.Core;

internal static class UpdateVerification
{
    public static void Run(Action<bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<bool, string> check)
    {
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string asset = "{\"name\":\"HSRUnlocker-1.2.3-win-x64-Setup.exe\",\"size\":12345,\"digest\":\"sha256:" + digest +
            "\",\"browser_download_url\":\"https://attacker.invalid/payload\"}";
        using (var client = Client(_ => Json(Release("v1.2.3", "\"assets\":[" + asset + "],"))))
        {
            UpdateCheckResult result = await new UpdateChecker(client).CheckAsync("1.1.0");
            check(result.Installer != null, "latest release exposes its matching hashed Windows installer");
            check(result.Installer.Version == "1.2.3" && result.Installer.FileName == "HSRUnlocker-1.2.3-win-x64-Setup.exe"
                && result.Installer.Size == 12345 && result.Installer.Sha256 == digest,
                "installer metadata preserves validated version, filename, size and SHA256");
            check(result.Installer.DownloadUri.AbsoluteUri ==
                "https://github.com/L1angYun/HSRUnlocker/releases/download/v1.2.3/HSRUnlocker-1.2.3-win-x64-Setup.exe",
                "installer URL is built from the official repository instead of remote arbitrary URLs");
        }
        using (var client = Client(_ => Json(Release("v1.0.0", "\"assets\":[{\"name\":\"HSRUnlocker-1.0.0-win-x64.zip\"}],"))))
            check((await new UpdateChecker(client).CheckAsync("1.1.0")).Installer == null,
                "older ZIP-only releases remain checkable without offering self-update");

        foreach (string invalidAsset in new[]
        {
            asset.Replace("\"size\":12345", "\"size\":0"),
            asset.Replace("\"size\":12345", "\"size\":536870913"),
            asset.Replace("\"size\":12345", "\"size\":1.5"),
            asset.Replace("\"size\":12345", "\"size\":\"12345\""),
            asset.Replace("sha256:" + digest, "sha256:1234"),
            asset.Replace("sha256:" + digest, "sha512:" + digest),
            asset.Replace("sha256:" + digest, "sha256:" + new string('z', 64)),
            asset.Replace(",\"digest\":\"sha256:" + digest + "\"", ""),
            asset + "," + asset
        })
            await Reject(check, Release("v1.2.3", "\"assets\":[" + invalidAsset + "],"),
                "installer with ambiguous or malformed size or integrity metadata is rejected");
        await Reject(check, Release("v1.2.3", "\"assets\":{},"),
            "a malformed release asset collection is rejected");

        foreach (var sample in new (string Current, string Tag, bool Newer)[]
        {
            ("1.1.0", "v1.2.3", true),
            ("1.9.0", "v1.10.0", true),
            ("1.1.0", "v2.0.0", true),
            ("1.1.0", "v1.1.0", false),
            ("1.1.0", "v1.0.9", false),
            ("1.1.0.0", "1.1.0", false),
            ("1.1.0", "v1.1.0.0", false),
            ("1.1.0", "v1.1.0.1", true),
            ("1.1.0.2", "v1.1.0.1", false),
            ("v1.1.0", "1.2.0", true)
        })
        {
            using var client = Client(_ => Json(Release(sample.Tag)));
            UpdateCheckResult result = await new UpdateChecker(client).CheckAsync(sample.Current);
            check(result.IsNewer == sample.Newer, $"release {sample.Tag} compares numerically with {sample.Current}");
            check(result.Version == sample.Tag.TrimStart('v'), $"release {sample.Tag} exposes its numeric display version");
        }

        int requestCount = 0;
        using (var client = Client(request =>
        {
            if (++requestCount > 1) return Json("{}");
            check(request.Method == HttpMethod.Get &&
                request.RequestUri.AbsoluteUri == "https://api.github.com/repos/L1angYun/HSRUnlocker/releases/latest",
                "update request uses the official repository latest-release endpoint");
            check(request.Headers.Accept.Any(value => value.MediaType == "application/vnd.github+json")
                && request.Headers.UserAgent.Count > 0
                && request.Headers.GetValues("X-GitHub-Api-Version").Single() == "2026-03-10",
                "update request supplies GitHub JSON, API version and User-Agent headers");
            check(request.Headers.Authorization == null, "update request sends no credentials");
            return Json(Release("v1.2.3", "\"html_url\":\"https://attacker.invalid/payload\","));
        }))
        {
            UpdateCheckResult result = await new UpdateChecker(client).CheckAsync("1.1.0");
            check(result.ReleaseUri.AbsoluteUri == "https://github.com/L1angYun/HSRUnlocker/releases/tag/v1.2.3",
                "release link is constructed from the trusted repository and validated tag");
            check(client.DefaultRequestHeaders.Count() == 0, "update checking preserves shared client default headers");
            using HttpResponseMessage reusable = await client.GetAsync("https://example.invalid/");
            check(reusable.IsSuccessStatusCode, "update checker leaves caller-owned HttpClient usable");
        }

        foreach (string tag in new[]
        {
            "", "v", "1", "1.2", "v1.2.3-beta", "1.2.3+build", "v1.2.3/evil",
            " v1.2.3", "v1.2.3 ", "v01.2.3", "V1.2.3", "1.2.-3", "1.2.3.4.5",
            "2147483648.0.0"
        })
            await Reject(check, Release(tag), $"unstable or malformed release tag '{tag}' is rejected");

        foreach (string body in new[]
        {
            "not json", "[]", "null", "{}", "{\"tag_name\":null,\"draft\":false,\"prerelease\":false}",
            "{\"tag_name\":123,\"draft\":false,\"prerelease\":false}",
            "{\"tag_name\":\"v1.2.3\",\"draft\":true,\"prerelease\":false}",
            "{\"tag_name\":\"v1.2.3\",\"draft\":false,\"prerelease\":true}",
            "{\"tag_name\":\"v1.2.3\",\"prerelease\":false}",
            "{\"tag_name\":\"v1.2.3\",\"draft\":false}",
            "{\"tag_name\":\"v1.2.3\",\"draft\":\"false\",\"prerelease\":false}"
        })
            await Reject(check, body, "invalid, draft, prerelease or incomplete release JSON is rejected");

        foreach (HttpStatusCode status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
        {
            using var client = Client(_ => new HttpResponseMessage(status) { Content = new StringContent("{}") });
            bool rejected = false;
            try { await new UpdateChecker(client).CheckAsync("1.1.0"); }
            catch (HttpRequestException ex) { rejected = ex.StatusCode == status; }
            check(rejected, $"HTTP {(int)status} fails with its HTTP status preserved");
        }

        await Reject(check, new string(' ', 262145) + Release("v1.2.3"),
            "oversize response with declared length is rejected");
        using (var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnseekableMemoryStream(Encoding.UTF8.GetBytes(new string(' ', 262145) + Release("v1.2.3"))))
        }))
        {
            bool rejected = false;
            try { await new UpdateChecker(client).CheckAsync("1.1.0"); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "oversize streamed response is rejected even without trusting declared length");
        }

        using (var cancellation = new CancellationTokenSource())
        using (var client = new HttpClient(new FixtureHandler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable fixture branch");
        })))
        {
            bool canceled = false;
            try { await new UpdateChecker(client).CheckAsync("1.1.0", cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled, "in-flight caller cancellation propagates through the HTTP request");
        }

        using (var cancellation = new CancellationTokenSource())
        using (var client = Client(_ => throw new InvalidOperationException("Canceled requests must not send")))
        {
            cancellation.Cancel();
            bool canceled = false;
            try { await new UpdateChecker(client).CheckAsync("1.1.0", cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled, "already canceled update checks do not send requests");
        }

        using (var client = new HttpClient(new FixtureHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable fixture branch");
        })) { Timeout = Timeout.InfiniteTimeSpan })
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            bool canceled = false;
            try { await new UpdateChecker(client).CheckAsync("1.1.0"); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled && timer.Elapsed < TimeSpan.FromSeconds(20),
                "update checker bounds a stalled request independently of the caller's HttpClient timeout");
        }
    }

    private static string Release(string tag, string extra = "") =>
        "{\"tag_name\":\"" + tag + "\"," + extra + "\"draft\":false,\"prerelease\":false}";

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new FixtureHandler((request, _) => Task.FromResult(respond(request))));

    private static async Task Reject(Action<bool, string> check, string body, string description)
    {
        using var client = Client(_ => Json(body));
        bool rejected = false;
        try { await new UpdateChecker(client).CheckAsync("1.1.0"); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, description);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;
        public FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => this.respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class UnseekableMemoryStream : MemoryStream
    {
        public UnseekableMemoryStream(byte[] bytes) : base(bytes) { }
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }
}
