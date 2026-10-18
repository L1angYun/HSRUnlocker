using System.Net;
using System.Net.Http;
using System.Text;
using StarRailUnlocker.Core;

internal static class UpdateMirrorCheckVerification
{
    private const string ListUri = "https://gitee.com/api/v5/repos/liangyun_Liu/HSRUnlocker/releases?direction=desc&per_page=100";
    private const string ManifestUri = "https://gitee.com/liangyun_Liu/HSRUnlocker/releases/download/v1.2.3/update.json";
    private const string AttachmentUri = "https://gitee.com/liangyun_Liu/HSRUnlocker/attach_files/123/download/update.json";
    private const string CdnUri = "https://foruda.gitee.com/attach_file/456/update.json?signature=fixture-private-signature";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static void Run(Action<bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<bool, string> check)
    {
        await VerifyConcurrentStartAsync(check);
        await VerifySelectionAsync(check);
        await VerifyModesAsync(check);
        await VerifyTrustTransitionsAsync(check);
        foreach (string failure in new[] { "connection", "timeout", "HTTP 503", "HTTP 403" })
        {
            var requests = new List<string>();
            using var client = Client(requests, (request, _) =>
            {
                if (request.RequestUri.Host == "api.github.com")
                {
                    if (failure == "connection") throw new HttpRequestException("Fixture connection failure.");
                    if (failure == "timeout") throw new TaskCanceledException("Fixture transport timeout.");
                    return Task.FromResult(new HttpResponseMessage(failure == "HTTP 503"
                        ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Forbidden));
                }
                return Task.FromResult(Json(request.RequestUri.AbsoluteUri == ListUri ? Releases("v1.2.3") : Manifest("v1.2.3")));
            });
            UpdateCheckResult result = null;
            try { result = await new UpdateChecker(client).CheckAsync("1.1.0"); }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) { }
            check(result?.IsNewer == true && result.Version == "1.2.3"
                && result.ReleaseUri.AbsoluteUri == "https://gitee.com/liangyun_Liu/HSRUnlocker/releases/tag/v1.2.3"
                && result.Installer?.DownloadUri.AbsoluteUri ==
                    "https://gitee.com/liangyun_Liu/HSRUnlocker/releases/download/v1.2.3/HSRUnlocker-1.2.3-win-x64-Setup.exe"
                && result.Installer.Size == 12345 && result.Installer.Sha256 == Hash
                && requests.SequenceEqual(new[]
                {
                    "https://api.github.com/repos/L1angYun/HSRUnlocker/releases/latest", ListUri, ManifestUri
                }), $"GitHub {failure} switches metadata and the exact verified package to Gitee");
        }

        var quotaRequests = new List<string>();
        using (var client = Client(quotaRequests, (request, _) =>
        {
            if (request.RequestUri.Host == "api.github.com")
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
                response.Headers.Add("X-RateLimit-Remaining", "0");
                return Task.FromResult(response);
            }
            if (request.RequestUri.Host == "github.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(Json(request.RequestUri.AbsoluteUri == ListUri ? Releases("v1.2.3") : Manifest("v1.2.3")));
        }))
            check((await new UpdateChecker(client).CheckAsync("1.1.0")).IsNewer && quotaRequests.Count == 4,
                "an unavailable GitHub quota manifest falls through to the Gitee release manifest");

        var redirects = new List<string>();
        using (var client = Client(redirects, (request, _) => Task.FromResult(request.RequestUri.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : request.RequestUri.AbsoluteUri == ListUri ? Json(Releases("v1.2.3"))
            : request.RequestUri.AbsoluteUri == ManifestUri ? Redirect(AttachmentUri)
            : request.RequestUri.AbsoluteUri == AttachmentUri ? Redirect(CdnUri)
            : Json(Manifest("v1.2.3")))))
        {
            var result = await new UpdateChecker(client).CheckAsync("1.2.3");
            check(!result.IsNewer && redirects.SequenceEqual(new[]
            {
                "https://api.github.com/repos/L1angYun/HSRUnlocker/releases/latest", ListUri, ManifestUri, AttachmentUri, CdnUri
            }), "Gitee metadata follows only its repository attachment and signed CDN while preserving version comparison");
        }

        var sorted = new List<string>();
        using (var client = Client(sorted, (request, _) => Task.FromResult(request.RequestUri.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : request.RequestUri.AbsoluteUri == ListUri
                ? Json("[" + Row("v1.9.0") + "," + Row("v1.10.0") + "," + Row("v2.0.0-preview") + "]")
                : Json(Manifest("v1.10.0")))))
            check((await new UpdateChecker(client).CheckAsync("1.9.0")).Version == "1.10.0"
                && sorted[^1] == "https://gitee.com/liangyun_Liu/HSRUnlocker/releases/download/v1.10.0/update.json",
                "Gitee selects the highest stable numeric release rather than last-updated or lexical ordering");

        foreach (string body in new[]
        {
            Manifest("v1.2.2"),
            Manifest("v1.2.3").Replace(Hash, "bad"),
            Manifest("v1.2.3").Replace("\"draft\":false", "\"draft\":true"),
            Manifest("v1.2.3").Replace("\"prerelease\":false", "\"prerelease\":true")
        })
        {
            var requests = new List<string>();
            using var client = Client(requests, (request, _) => Task.FromResult(request.RequestUri.Host == "api.github.com"
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : Json(request.RequestUri.AbsoluteUri == ListUri ? Releases("v1.2.3") : body)));
            bool rejected = false;
            try { await new UpdateChecker(client).CheckAsync("1.1.0", UpdateSourcePreference.Gitee); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected && requests.Count == 2, "Gitee cannot substitute a different version, missing digest or unstable manifest");
        }

        foreach (string target in new[]
        {
            "http://gitee.com/liangyun_Liu/HSRUnlocker/attach_files/123/download/update.json",
            "https://attacker.invalid/update.json",
            "https://gitee.com/other/project/attach_files/123/download/update.json",
            "https://foruda.gitee.com.evil.invalid/attach_file/456/update.json",
            "https://foruda.gitee.com/attach_file/456/other.json",
            "https://user@foruda.gitee.com/attach_file/456/update.json",
            "https://foruda.gitee.com:444/attach_file/456/update.json"
        })
        {
            var requests = new List<string>();
            using var client = Client(requests, (request, _) => Task.FromResult(request.RequestUri.Host == "api.github.com"
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : request.RequestUri.AbsoluteUri == ListUri ? Json(Releases("v1.2.3")) : Redirect(target)));
            bool rejected = false;
            try { await new UpdateChecker(client).CheckAsync("1.1.0", UpdateSourcePreference.Gitee); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected && requests.Count == 2, "unsafe Gitee metadata redirects are rejected before any target request");
        }

        using (var cancellation = new CancellationTokenSource())
        {
            var requests = new List<string>();
            using var client = Client(requests, async (request, token) =>
            {
                if (request.RequestUri.Host == "api.github.com") return new HttpResponseMessage(HttpStatusCode.BadGateway);
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Unreachable fixture branch.");
            });
            bool canceled = false;
            try { await new UpdateChecker(client).CheckAsync("1.1.0", cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled && requests.Count == 2, "caller cancellation during Gitee fallback stops without fetching a manifest");
        }
    }

    private static async Task VerifySelectionAsync(Action<bool, string> check)
    {
        // Breaks caught: always preferring GitHub, first-result-only selection, lexical versions,
        // ignoring installer availability, or accepting conflicting same-version manifests.
        foreach (var sample in new (bool GiteeFirst, string First, string Other, bool FirstInstaller, bool OtherInstaller, string Conflict, string Host)[]
        {
            (true, "v1.2.3", "v1.2.3", true, true, "", "gitee.com"),
            (false, "v1.2.3", "v1.2.3", true, true, "", "github.com"),
            (true, "v1.9.0", "v1.10.0", true, true, "", "github.com"),
            (false, "v1.9.0", "v1.10.0", true, true, "", "gitee.com"),
            (true, "v1.10.0", "v1.9.0", true, true, "", "gitee.com"),
            (true, "v1.2.3", "v1.2.3", false, true, "", "github.com"),
            (false, "v1.2.3", "v1.2.3", false, true, "", "gitee.com"),
            (true, "v1.2.3", "v1.2.3", true, true, "hash", ""),
            (false, "v1.2.3", "v1.2.3", true, true, "size", "")
        })
        {
            var firstGate = Signal();
            var otherGate = Signal();
            var firstDisposed = Signal();
            var requests = new List<string>();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = Client(requests, async (request, token) =>
            {
                bool gitee = request.RequestUri.Host == "gitee.com";
                bool first = gitee == sample.GiteeFirst;
                await (first ? firstGate : otherGate).Task.WaitAsync(token);
                string tag = first ? sample.First : sample.Other;
                if (request.RequestUri.AbsoluteUri == ListUri) return Json(Releases(tag));
                string body = (first ? sample.FirstInstaller : sample.OtherInstaller)
                    ? Manifest(tag) : "{\"tag_name\":\"" + tag + "\",\"draft\":false,\"prerelease\":false,\"assets\":[]}";
                if (!first && sample.Conflict == "hash") body = body.Replace(Hash, new string('a', 64));
                if (!first && sample.Conflict == "size") body = body.Replace("12345", "12346");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = first ? new ObservedContent(body, firstDisposed) : new StringContent(body, Encoding.UTF8, "application/json")
                };
            });
            Task<UpdateCheckResult> operation = new UpdateChecker(client).CheckAsync("1.0.0", cancellation.Token);
            firstGate.SetResult();
            await firstDisposed.Task.WaitAsync(cancellation.Token);
            // The response-disposal signal gates the second provider after the first verified read.
            await Task.Delay(30, cancellation.Token);
            otherGate.SetResult();
            UpdateCheckResult result = null;
            bool conflict = false;
            try { result = await operation; }
            catch (InvalidDataException) { conflict = true; }
            check(sample.Conflict.Length != 0 ? conflict : !conflict && result?.ReleaseUri.Host == sample.Host
                && result.Version == (sample.First == "v1.10.0" || sample.Other == "v1.10.0" ? "1.10.0" : "1.2.3"),
                sample.Conflict.Length != 0
                    ? $"same-version installer {sample.Conflict} conflict fails closed"
                    : $"Auto compares completed releases and selects {sample.Host} when {(sample.GiteeFirst ? "Gitee" : "GitHub")} finishes first ({sample.First}/{sample.Other}, installers {sample.FirstInstaller}/{sample.OtherInstaller})");
        }

        // Break caught: waiting the full eight-second mirror timeout after a usable result.
        var stalledStarted = Signal();
        var stalledCanceled = Signal();
        var stalledRequests = new List<string>();
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
        using (var client = Client(stalledRequests, async (request, token) =>
        {
            if (request.RequestUri.Host == "api.github.com")
            {
                await stalledStarted.Task.WaitAsync(token);
                return Json(Manifest("v1.2.3"));
            }
            stalledStarted.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stalledCanceled.TrySetResult(); }
            throw new InvalidOperationException("Unreachable stalled source.");
        }))
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            UpdateCheckResult result = await new UpdateChecker(client).CheckAsync("1.0.0", cancellation.Token);
            check(result.ReleaseUri.Host == "github.com" && stalledCanceled.Task.IsCompleted
                && timer.Elapsed < TimeSpan.FromSeconds(2.5),
                "Auto cancels and observes a stalled second source after the one-second collection bound");
        }

        // Break caught: cancellation reporting a completed provider's old error instead of caller cancellation.
        var bothStarted = Signal();
        int started = 0, stopped = 0;
        var cancelRequests = new List<string>();
        using (var cancellation = new CancellationTokenSource())
        using (var client = Client(cancelRequests, async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Interlocked.Increment(ref stopped); }
            throw new InvalidOperationException("Unreachable canceled source.");
        }))
        {
            Task<UpdateCheckResult> operation = new UpdateChecker(client).CheckAsync("1.0.0", cancellation.Token);
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            bool canceled = false;
            try { await operation; }
            catch (OperationCanceledException ex) { canceled = ex.CancellationToken == cancellation.Token; }
            check(canceled && stopped == 2 && cancelRequests.Count == 2,
                "caller cancellation propagates its token after both concurrent providers have stopped");
        }

        foreach (bool invalidGitee in new[] { false, true })
        {
            // Break caught: malformed metadata at one provider poisoning an independently verified provider.
            var requests = new List<string>();
            using var client = Client(requests, (request, _) =>
            {
                bool gitee = request.RequestUri.Host == "gitee.com";
                return Task.FromResult(Json(request.RequestUri.AbsoluteUri == ListUri
                    ? Releases("v1.2.3") : gitee == invalidGitee ? "invalid-json" : Manifest("v1.2.3")));
            });
            UpdateCheckResult result = await new UpdateChecker(client).CheckAsync("1.0.0");
            check(result.ReleaseUri.Host == (invalidGitee ? "github.com" : "gitee.com"),
                "an invalid source is ineligible while the independently verified source remains usable");
        }

        // Break caught: treating the first failure as the start of the success-collection window.
        var remainingStarted = Signal();
        var releaseRemaining = Signal();
        var remainingRequests = new List<string>();
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
        using (var client = Client(remainingRequests, async (request, token) =>
        {
            if (request.RequestUri.Host == "api.github.com")
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (request.RequestUri.AbsoluteUri == ListUri)
            {
                remainingStarted.SetResult();
                await releaseRemaining.Task.WaitAsync(token);
                return Json(Releases("v1.2.3"));
            }
            return Json(Manifest("v1.2.3"));
        }))
        {
            Task<UpdateCheckResult> operation = new UpdateChecker(client).CheckAsync("1.0.0", cancellation.Token);
            await remainingStarted.Task;
            await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellation.Token);
            bool stillWaiting = !operation.IsCompleted;
            releaseRemaining.SetResult();
            check(stillWaiting && (await operation).ReleaseUri.Host == "gitee.com",
                "a first-source failure waits for the remaining provider's own deadline, not the success window");
        }

        // Break caught: a rejected source's unsafe redirect contaminating the other verified result.
        var unsafeRequests = new List<string>();
        using (var client = Client(unsafeRequests, (request, _) => Task.FromResult(
            request.RequestUri.Host == "api.github.com" ? Json(Manifest("v1.2.3"))
                : request.RequestUri.AbsoluteUri == ListUri ? Json(Releases("v1.2.3"))
                : Redirect("https://attacker.invalid/update.json"))))
            check((await new UpdateChecker(client).CheckAsync("1.0.0")).ReleaseUri.Host == "github.com"
                && unsafeRequests.Count == 3, "an unsafe source is excluded before its target while the verified source remains selectable");

        // Break caught: forwarding signed CDN query text through either provider's exception chain.
        var failedRequests = new List<string>();
        using (var client = Client(failedRequests, (_, _) => throw new HttpRequestException(
            "Fixture transport failed: " + CdnUri, new IOException("Secret: " + CdnUri), HttpStatusCode.BadGateway)))
        {
            Exception failure = null;
            try { await new UpdateChecker(client).CheckAsync("1.0.0"); }
            catch (HttpRequestException error) { failure = error; }
            check(failure != null && failedRequests.Count == 2
                && !failure.ToString().Contains("fixture-private-signature", StringComparison.Ordinal),
                "two failed concurrent sources expose safe HTTP diagnostics without signed queries in inner exceptions");
        }

        // Break caught: awaiting one faulted source in cleanup skips observation of the other source.
        var cleanupEntered = Signal();
        var releaseCleanup = Signal();
        var cleanupRequests = new List<string>();
        using (var client = Client(cleanupRequests, async (request, token) =>
        {
            if (request.RequestUri.Host == "api.github.com")
                throw new InvalidOperationException("Fixture unexpected provider fault.");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                cleanupEntered.SetResult();
                await releaseCleanup.Task;
            }
            throw new InvalidOperationException("Fixture unexpected cleanup fault.");
        }))
        {
            Task<UpdateCheckResult> operation = new UpdateChecker(client).CheckAsync("1.0.0");
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            bool observedAll = !operation.IsCompleted;
            releaseCleanup.SetResult();
            try { await operation; }
            catch (InvalidOperationException) { }
            check(observedAll, "provider faults still observe all pending canceled work before the check finishes");
        }
    }

    private static async Task VerifyModesAsync(Action<bool, string> check)
    {
        // Break caught: fixed preferences still contacting the unselected provider or losing download policy.
        foreach (UpdateSourcePreference mode in new[] { UpdateSourcePreference.GitHub, UpdateSourcePreference.Gitee })
        {
            var requests = new List<string>();
            using var client = Client(requests, (request, _) => Task.FromResult(
                Json(request.RequestUri.AbsoluteUri == ListUri ? Releases("v1.2.3") : Manifest("v1.2.3"))));
            UpdateCheckResult result = await new UpdateChecker(client).CheckAsync("1.0.0", mode);
            check(result.Installer.Preference == mode
                && requests.All(uri => new Uri(uri).Host == (mode == UpdateSourcePreference.GitHub ? "api.github.com" : "gitee.com"))
                && requests.Count == (mode == UpdateSourcePreference.GitHub ? 1 : 2),
                $"{mode}-only checks only that provider and retains the fixed download preference");
        }
        using (var client = Client(new List<string>(), (_, _) => throw new InvalidOperationException("Invalid enum sent a request.")))
        {
            bool rejected = false;
            try { await new UpdateChecker(client).CheckAsync("1.0.0", (UpdateSourcePreference)123); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected, "undefined update source preferences fail before any HTTP work");
        }
    }

    private static async Task VerifyTrustTransitionsAsync(Action<bool, string> check)
    {
        // Break caught: trusting an allowlisted CDN without binding it to the owned attachment hop,
        // or permitting attachment/CDN transitions to backtrack or loop.
        foreach (var sample in new (string Hop, string Target, int Count)[]
        {
            ("release", CdnUri, 2),
            ("attachment", ManifestUri, 3),
            ("attachment", AttachmentUri, 3),
            ("cdn", AttachmentUri, 4),
            ("cdn", CdnUri, 4),
            ("release", "https://gitee.com/liangyun_Liu/HSRUnlocker/releases/download/v1.2.4/update.json", 2)
        })
        {
            var requests = new List<string>();
            using var client = Client(requests, (request, _) =>
            {
                string uri = request.RequestUri.AbsoluteUri;
                return Task.FromResult(uri == ListUri ? Json(Releases("v1.2.3"))
                    : uri == ManifestUri ? Redirect(sample.Hop == "release" ? sample.Target : AttachmentUri)
                    : uri == AttachmentUri ? Redirect(sample.Hop == "attachment" ? sample.Target : CdnUri)
                    : Redirect(sample.Target));
            });
            bool refused = false;
            try { await new UpdateChecker(client).CheckAsync("1.0.0", UpdateSourcePreference.Gitee); }
            catch (InvalidDataException) { refused = true; }
            check(refused && requests.Count == sample.Count,
                $"Gitee rejects an invalid {sample.Hop} transition before requesting its target");
        }

        foreach (string releases in new[]
        {
            "[" + Row("v2.0.0") + "," + Row("v1.2.3") + "," + Row("1.2.3") + "]",
            "[" + Row("v1.2.3").Replace("{\"name\":\"update.json\",", "{\"name\":\"update.json\"},{\"name\":\"update.json\",") + "]",
            new string(' ', 262145) + Releases("v1.2.3")
        })
        {
            // Break caught: only comparing the selected highest version leaves lower equivalent versions ambiguous.
            var requests = new List<string>();
            using var client = Client(requests, (request, _) => Task.FromResult(
                Json(request.RequestUri.AbsoluteUri == ListUri ? releases : Manifest("v2.0.0"))));
            bool refused = false;
            try { await new UpdateChecker(client).CheckAsync("1.0.0", UpdateSourcePreference.Gitee); }
            catch (InvalidDataException) { refused = true; }
            check(refused && requests.Count == 1, "ambiguous or oversized Gitee lists fail before any manifest request");
        }

        var incompleteRequests = new List<string>();
        using (var client = Client(incompleteRequests, (request, _) => Task.FromResult(Json(
            request.RequestUri.AbsoluteUri == ListUri
                ? "[" + Row("v3.0.0").Replace("\"update.json\"", "\"source.zip\"") + ","
                    + Row("v2.0.0").Replace("\"prerelease\":false", "\"prerelease\":true") + "," + Row("v1.2.3") + "]"
                : Manifest("v1.2.3")))))
            check((await new UpdateChecker(client).CheckAsync("1.0.0", UpdateSourcePreference.Gitee)).Version == "1.2.3",
                "incomplete uploads and prereleases cannot hide the highest complete stable Gitee release");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ObservedContent : StringContent
    {
        private readonly TaskCompletionSource disposed;
        internal ObservedContent(string body, TaskCompletionSource disposed) : base(body, Encoding.UTF8, "application/json") =>
            this.disposed = disposed;
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) disposed.TrySetResult();
        }
    }

    private static async Task VerifyConcurrentStartAsync(Action<bool, string> check)
    {
        // Break caught: awaiting GitHub before starting Gitee makes Auto serial.
        var githubStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var giteeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<string>();
        using var cancellation = new CancellationTokenSource();
        using var client = Client(requests, async (request, token) =>
        {
            if (request.RequestUri.Host == "api.github.com") githubStarted.TrySetResult();
            else giteeStarted.TrySetResult();
            await release.Task.WaitAsync(token);
            return Json(request.RequestUri.AbsoluteUri == ListUri ? Releases("v1.2.3") : Manifest("v1.2.3"));
        });
        Task<UpdateCheckResult> operation = new UpdateChecker(client).CheckAsync("1.1.0", cancellation.Token);
        Task bothStarted = Task.WhenAll(githubStarted.Task, giteeStarted.Task);
        bool concurrent = await Task.WhenAny(bothStarted, Task.Delay(TimeSpan.FromSeconds(2))) == bothStarted;
        if (!concurrent) cancellation.Cancel();
        release.TrySetResult();
        try { await operation; }
        catch (OperationCanceledException) when (!concurrent) { }
        check(concurrent, "Auto starts both provider requests before awaiting either response");
    }

    private static string Row(string tag) =>
        "{\"tag_name\":\"" + tag + "\",\"prerelease\":false,\"assets\":[{\"name\":\"update.json\",\"browser_download_url\":\"https://attacker.invalid/ignored\"}]}";
    private static string Releases(string tag) => "[" + Row(tag) + "]";
    private static string Manifest(string tag)
    {
        string version = tag.TrimStart('v');
        return "{\"tag_name\":\"" + tag + "\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"HSRUnlocker-"
            + version + "-win-x64-Setup.exe\",\"size\":12345,\"digest\":\"sha256:" + Hash + "\"}]}";
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Redirect(string target)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(target);
        return response;
    }
    private static HttpClient Client(List<string> requests, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new FixtureHandler(requests, respond));
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly List<string> requests;
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;
        internal FixtureHandler(List<string> requests, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            { this.requests = requests; this.respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Headers.Authorization != null || request.RequestUri.Query.Contains("access_token", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Public update requests must never contain credentials.");
            lock (requests) requests.Add(request.RequestUri.AbsoluteUri);
            return respond(request, token);
        }
    }
}
