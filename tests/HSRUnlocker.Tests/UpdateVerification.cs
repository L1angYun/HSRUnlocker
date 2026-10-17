using System.Net;
using System.Net.Http;
using System.Text;
using StarRailUnlocker.Core;

internal static class UpdateVerification
{
    public static void Run(Action<bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<bool, string> check)
    {
        int quotaRequests = 0;
        using (var client = Client(request =>
        {
            quotaRequests++;
            if (request.RequestUri.Host == "api.github.com")
            {
                var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
                limited.Headers.Add("X-RateLimit-Remaining", "0");
                limited.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString());
                return limited;
            }
            check(request.RequestUri.AbsoluteUri == "https://github.com/L1angYun/HSRUnlocker/releases/latest/download/update.json",
                "quota fallback requests the same repository's first-party release manifest");
            return Json(Release("v1.2.3"));
        }))
        {
            UpdateCheckResult result = await new UpdateChecker(client).CheckAsync("1.1.0");
            check(result.IsNewer && result.Version == "1.2.3" && quotaRequests == 2,
                "an exhausted anonymous API quota still detects the accessible first-party release");
        }
        await VerifyRateLimitFallbackAsync(check);

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

    private static async Task VerifyRateLimitFallbackAsync(Action<bool, string> check)
    {
        DateTimeOffset now = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        const string manifest = "https://github.com/L1angYun/HSRUnlocker/releases/latest/download/update.json";
        const string tagged = "https://github.com/L1angYun/HSRUnlocker/releases/download/v1.2.3/update.json";
        const string signed = "https://release-assets.githubusercontent.com/github-production-release-asset/fixture/update.json?sig=fixture-private-signature";
        int calls = 0;
        using (var client = Client(request =>
        {
            calls++;
            if (calls == 1) return Quota(HttpStatusCode.Forbidden);
            check(request.Headers.Authorization == null && request.Headers.UserAgent.Count > 0,
                "fallback requests retain a User-Agent and send no authorization");
            if (request.RequestUri.AbsoluteUri == manifest) return Redirect("/L1angYun/HSRUnlocker/releases/download/v1.2.3/update.json");
            if (request.RequestUri.AbsoluteUri == tagged) return Redirect(signed);
            check(request.RequestUri.AbsoluteUri == signed, "manual redirects retain the first-party CDN's signed query");
            var response = Json(Release("v1.2.3", "\"assets\":[{\"name\":\"HSRUnlocker-1.2.3-win-x64-Setup.exe\",\"size\":12,\"digest\":\"sha256:" + new string('a', 64) + "\"}],"));
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri);
            return response;
        }))
        {
            UpdateCheckResult result = await new UpdateChecker(client).CheckAsync("1.1.0");
            check(calls == 4 && result.Installer?.Sha256 == new string('a', 64)
                && result.Installer.DownloadUri.AbsoluteUri ==
                    "https://github.com/L1angYun/HSRUnlocker/releases/download/v1.2.3/HSRUnlocker-1.2.3-win-x64-Setup.exe",
                "first-party release redirects preserve the same exact installer hash and trusted URL validation");
            check(client.DefaultRequestHeaders.Count() == 0, "fallback redirects leave shared client default headers unchanged");
        }

        int apiCalls = 0, manifestCalls = 0;
        using (var client = Client(request =>
        {
            if (request.RequestUri.Host == "api.github.com")
            {
                if (++apiCalls > 1) return Json(Release("v1.2.3"));
                var quota = Quota(HttpStatusCode.TooManyRequests);
                quota.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                quota.Headers.Add("X-RateLimit-Reset", now.AddMinutes(3).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                return quota;
            }
            manifestCalls++;
            return Json(Release("v1.2.3"));
        }))
        {
            var checker = new UpdateChecker(client, () => now);
            await checker.CheckAsync("1.1.0");
            await checker.CheckAsync("1.1.0");
            check(apiCalls == 1 && manifestCalls == 2, "same-instance checks bypass the exhausted API until its retry/reset deadline");
            now = now.AddMinutes(4);
            await checker.CheckAsync("1.1.0");
            check(apiCalls == 2 && manifestCalls == 2, "API checks resume after the stored quota cooldown expires");
        }

        foreach (var sample in new (string Header, string Value, TimeSpan Wait)[]
        {
            ("", "", TimeSpan.FromMinutes(1)),
            ("Retry-After", "120", TimeSpan.FromMinutes(2)),
            ("Retry-After", now.AddMinutes(3).ToString("R"), TimeSpan.FromMinutes(3)),
            ("X-RateLimit-Reset", now.AddMinutes(5).ToUnixTimeSeconds().ToString(), TimeSpan.FromMinutes(5)),
            ("X-RateLimit-Reset", "9223372036854775807", TimeSpan.FromMinutes(1)),
            ("Retry-After", "8640000", TimeSpan.FromHours(24))
        })
        {
            int requests = 0;
            using var client = Client(_ =>
            {
                if (++requests > 1) return new HttpResponseMessage(HttpStatusCode.NotFound);
                var response = Quota(HttpStatusCode.TooManyRequests);
                if (sample.Header.Length != 0) response.Headers.TryAddWithoutValidation(sample.Header, sample.Value);
                return response;
            });
            UpdateRateLimitException error = await RateFailure(new UpdateChecker(client, () => now));
            check(error != null && error.StatusCode == HttpStatusCode.TooManyRequests && error.RetryAt == now + sample.Wait
                && error.InnerException is HttpRequestException inner && inner.StatusCode == HttpStatusCode.NotFound,
                "quota fallback failure exposes the API status, bounded retry deadline and useful fallback HTTP status");
        }

        int secondaryCalls = 0;
        using (var client = Client(_ =>
        {
            if (++secondaryCalls > 1) return Json(Release("v1.2.3"));
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return response;
        }))
            check((await new UpdateChecker(client).CheckAsync("1.1.0")).IsNewer && secondaryCalls == 2,
                "a secondary-limit 403 with Retry-After also uses the first-party manifest");

        int ordinaryCalls = 0;
        using (var client = Client(_ =>
        {
            ordinaryCalls++;
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }))
        {
            bool preserved = false;
            try { await new UpdateChecker(client).CheckAsync("1.1.0"); }
            catch (HttpRequestException error) { preserved = error.StatusCode == HttpStatusCode.Forbidden && error is not UpdateRateLimitException; }
            check(preserved && ordinaryCalls == 1, "an ordinary 403 without quota headers preserves its status without attempting fallback");
        }

        int failedApiCalls = 0, failedManifestCalls = 0;
        using (var client = Client(request =>
        {
            if (request.RequestUri.Host == "api.github.com")
            {
                failedApiCalls++;
                return Quota(HttpStatusCode.Forbidden);
            }
            failedManifestCalls++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }))
        {
            var checker = new UpdateChecker(client, () => now);
            UpdateRateLimitException first = await RateFailure(checker);
            UpdateRateLimitException second = await RateFailure(checker);
            check(first?.RetryAt == second?.RetryAt && second?.StatusCode == HttpStatusCode.Forbidden
                && failedApiCalls == 1 && failedManifestCalls == 2,
                "a failed fallback still preserves the API cooldown and original quota diagnosis on the next check");
        }

        foreach (string target in new[]
        {
            "http://github.com/L1angYun/HSRUnlocker/releases/download/v1.2.3/update.json",
            "https://attacker.invalid/update.json",
            "https://github.com.evil.invalid/L1angYun/HSRUnlocker/releases/latest/download/update.json",
            "https://github.com/other/HSRUnlocker/releases/latest/download/update.json",
            "https://github.com/L1angYun/HSRUnlocker/releases/download/v1.2.3-beta/update.json",
            "https://github.com/L1angYun/HSRUnlocker/releases/download//update.json",
            "https://github.com/L1angYun/HSRUnlocker/releases/download/update.json",
            "https://github.com/L1angYun/HSRUnlocker/releases/download/v1.2.3/other.json",
            "https://github.com/L1angYun/HSRUnlocker/releases/latest/download/update.json?token=bad",
            "https://github.com/L1angYun/HSRUnlocker/releases/latest/download/update.json#fragment",
            "https://user@github.com/L1angYun/HSRUnlocker/releases/latest/download/update.json",
            "https://github.com:444/L1angYun/HSRUnlocker/releases/latest/download/update.json",
            "https://release-assets.githubusercontent.com/fixture?sig=bad#fragment",
            "https://user@release-assets.githubusercontent.com/fixture?sig=bad",
            "https://release-assets.githubusercontent.com:444/fixture?sig=bad",
            "https://release-assets.githubusercontent.com.evil.invalid/fixture?sig=bad",
            "http://release-assets.githubusercontent.com/fixture?sig=bad"
        })
        {
            int requests = 0;
            using var client = Client(_ =>
            {
                if (++requests == 1) return Quota(HttpStatusCode.Forbidden);
                if (requests == 2) return Redirect(target);
                throw new InvalidOperationException("Unsafe redirect targets must never be requested.");
            });
            UpdateRateLimitException error = await RateFailure(new UpdateChecker(client));
            check(error?.InnerException is InvalidDataException && requests == 2,
                "unsafe manifest redirect is rejected before sending any request to its target");
        }

        foreach (bool overflow in new[] { false, true })
        {
            int requests = 0;
            using var client = Client(_ =>
            {
                if (++requests == 1) return Quota(HttpStatusCode.Forbidden);
                if (overflow || requests < 7) return Redirect("https://release-assets.githubusercontent.com/fixture?hop=" + requests);
                return Json(Release("v1.2.3"));
            });
            if (overflow)
                check((await RateFailure(new UpdateChecker(client)))?.InnerException is InvalidDataException && requests == 7,
                    "a sixth metadata redirect fails after at most five manually followed hops");
            else
                check((await new UpdateChecker(client).CheckAsync("1.1.0")).IsNewer && requests == 7,
                    "five trusted HTTPS metadata redirects are allowed");
        }

        foreach (string actualUri in new[] { "https://attacker.invalid/update.json", signed })
        {
            int requests = 0;
            using var client = Client(_ =>
            {
                if (++requests == 1) return Quota(HttpStatusCode.Forbidden);
                var response = Json(Release("v1.2.3"));
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, actualUri);
                return response;
            });
            check((await RateFailure(new UpdateChecker(client)))?.InnerException is InvalidDataException,
                "an automatically followed response URI is rejected even when its host would otherwise be allowed");
        }
        {
            int requests = 0;
            using var client = Client(_ =>
            {
                requests++;
                var response = Quota(HttpStatusCode.Forbidden);
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, signed);
                return response;
            });
            bool rejected = false;
            try { await new UpdateChecker(client).CheckAsync("1.1.0"); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected && requests == 1, "an unexpected API response URI is rejected before trusting its quota headers");
        }

        foreach (string body in new[]
        {
            "not json", Release("v1.2.3-beta"),
            "{\"tag_name\":\"v1.2.3\",\"draft\":false,\"prerelease\":true}",
            Release("v1.2.3", "\"assets\":[{\"name\":\"HSRUnlocker-1.2.3-win-x64-Setup.exe\",\"size\":12,\"digest\":\"sha256:bad\"}],"),
            new string(' ', 262145) + Release("v1.2.3")
        })
        {
            int requests = 0;
            using var client = Client(_ => ++requests == 1 ? Quota(HttpStatusCode.Forbidden) : Json(body));
            check((await RateFailure(new UpdateChecker(client)))?.InnerException is InvalidDataException,
                "fallback release metadata retains stable-version, installer-integrity and bounded-response validation");
        }
        {
            int requests = 0;
            using var client = Client(_ => ++requests == 1 ? Quota(HttpStatusCode.Forbidden) :
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new UnseekableMemoryStream(Encoding.UTF8.GetBytes(new string(' ', 262145))))
                });
            check((await RateFailure(new UpdateChecker(client)))?.InnerException is InvalidDataException,
                "fallback streamed metadata is bounded even without a declared Content-Length");
        }

        using (var cancellation = new CancellationTokenSource())
        {
            int requests = 0;
            using var client = new HttpClient(new FixtureHandler(async (_, token) =>
            {
                if (++requests == 1) return Quota(HttpStatusCode.Forbidden);
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Unreachable fixture branch.");
            }));
            bool canceled = false;
            try { await new UpdateChecker(client).CheckAsync("1.1.0", cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled && requests == 2, "caller cancellation during quota fallback propagates without being wrapped as a rate-limit error");
        }
        {
            int requests = 0;
            using var client = Client(_ =>
            {
                if (++requests == 1) return Quota(HttpStatusCode.Forbidden);
                throw new HttpRequestException("Failure at " + signed, null, HttpStatusCode.BadGateway);
            });
            UpdateRateLimitException error = await RateFailure(new UpdateChecker(client));
            check(error != null && !error.ToString().Contains("fixture-private-signature")
                && error.InnerException is HttpRequestException inner && inner.StatusCode == HttpStatusCode.BadGateway,
                "fallback diagnostics retain useful HTTP status while withholding signed CDN URLs from logs");
        }
        {
            int requests = 0;
            using var client = new HttpClient(new FixtureHandler(async (_, token) =>
            {
                if (++requests == 1) return Quota(HttpStatusCode.Forbidden);
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Unreachable fixture branch.");
            })) { Timeout = TimeSpan.FromMilliseconds(100) };
            UpdateRateLimitException error = await RateFailure(new UpdateChecker(client));
            check(error?.InnerException is TimeoutException,
                "a fallback transport timeout produces the typed quota diagnosis while caller cancellation remains distinct");
        }
    }

    private static HttpResponseMessage Quota(HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        return response;
    }

    private static HttpResponseMessage Redirect(string target)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static async Task<UpdateRateLimitException> RateFailure(UpdateChecker checker)
    {
        try { await checker.CheckAsync("1.1.0"); }
        catch (UpdateRateLimitException error) { return error; }
        return null;
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
