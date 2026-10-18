using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using StarRailUnlocker.Core;

internal static class UpdateMirrorDownloadVerification
{
    private const string FileName = "HSRUnlocker-1.2.3-win-x64-Setup.exe";
    private const string GitHub = "https://github.com/L1angYun/HSRUnlocker/releases/download/v1.2.3/HSRUnlocker-1.2.3-win-x64-Setup.exe";
    private const string Gitee = "https://gitee.com/liangyun_Liu/HSRUnlocker/releases/download/v1.2.3/HSRUnlocker-1.2.3-win-x64-Setup.exe";
    private const string Attachment = "https://gitee.com/liangyun_Liu/HSRUnlocker/attach_files/123/download/HSRUnlocker-1.2.3-win-x64-Setup.exe";
    private const string Cdn = "https://foruda.gitee.com/attach_file/456/HSRUnlocker-1.2.3-win-x64-Setup.exe?fixture-signature=private-fixture-value";
    private const string LowercaseCdn = "https://foruda.gitee.com/attach_file/456/hsrunlocker-1.2.3-win-x64-setup.exe?fixture-signature=synthetic-lowercase-value";

    public static void Run(Action<bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "HSRUnlocker-mirror-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        byte[] payload = Enumerable.Range(0, 200000).Select(value => (byte)(value % 251)).ToArray();
        UpdatePackage package = Package(payload, GitHub);
        try
        {
            await VerifyLowercaseCdnDownloadAsync(check, root, payload);
            await VerifyLowercaseCdnIntegrityAsync(check, root, payload);
            await VerifyCdnRedirectBoundariesAsync(check, root, payload);
            await VerifyProtocolLengthFailureAsync(check, root, payload);
            // Break caught: fixed-source metadata still permits alternate-provider retry.
            foreach (var sample in new[]
            {
                (Url: GitHub, Mode: UpdateSourcePreference.GitHub),
                (Url: Gitee, Mode: UpdateSourcePreference.Gitee)
            })
            {
                var requests = new List<string>();
                using var client = Client(requests, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
                bool refused = false;
                try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(Package(payload, sample.Url, sample.Mode)); }
                catch (HttpRequestException) { refused = true; }
                check(refused && requests.SequenceEqual(new[] { sample.Url }) && IsEmpty(root),
                    $"{sample.Mode}-only downloads never retry another provider after a transport failure");
            }

            // Break caught: Gitee-selected Auto packages cannot recover through GitHub.
            var reverseRequests = new List<string>();
            using (var client = Client(reverseRequests, (request, _) => Task.FromResult(
                request.RequestUri.AbsoluteUri == Gitee ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Response(payload))))
                await CheckSuccessfulDownload(check, client, root, Package(payload, Gitee), payload, reverseRequests,
                    new[] { Gitee, GitHub }, "Auto Gitee-primary retries the exact selected package once through GitHub");

            // Break caught: relying on HttpClient's timeout or the overall ten-minute budget for a stuck source.
            foreach (bool stalledBody in new[] { false, true })
            {
                var requests = new List<string>();
                bool sourceClosed = !stalledBody, closedBeforeRetry = false;
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var client = Client(requests, async (request, token) =>
                {
                    if (request.RequestUri.AbsoluteUri == Gitee)
                    {
                        closedBeforeRetry = sourceClosed;
                        return Response(payload);
                    }
                    if (stalledBody)
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StreamContent(new StalledStream(payload, () => sourceClosed = true))
                        };
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException("Unreachable stalled header fixture.");
                });
                client.Timeout = Timeout.InfiniteTimeSpan;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                await CheckSuccessfulDownload(check, client, root, package, payload, requests,
                    new[] { GitHub, Gitee }, stalledBody
                        ? "an idle source stream times out and retries the alternate package"
                        : "stalled response headers time out independently of HttpClient and retry the alternate package",
                    cancellationToken: cancellation.Token);
                check(timer.Elapsed < TimeSpan.FromSeconds(11) && closedBeforeRetry && IsEmpty(root),
                    "a stalled source is bounded and its streams close before alternate-provider work starts");
            }

            // Break caught: catching all IOExceptions around an attempt misclassifies local staging failures.
            var localRequests = new List<string>();
            using (var client = Client(localRequests, (_, _) =>
            {
                string stage = Directory.EnumerateDirectories(root).Single();
                if (Path.GetDirectoryName(stage) != root || !Path.GetFileName(stage).StartsWith("HSRUnlocker-Update-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected local fixture directory.");
                Directory.Delete(stage);
                return Task.FromResult(Response(payload));
            }))
            {
                bool refused = false;
                try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(package); }
                catch (IOException) { refused = true; }
                check(refused && localRequests.SequenceEqual(new[] { GitHub }) && IsEmpty(root),
                    "a local staging file failure is never treated as a remote retryable IOException");
            }

            foreach (string failure in new[] { "DNS", "transport", "HTTP 503", "response-header timeout" })
            {
                var requests = new List<string>();
                using var client = Client(requests, async (request, token) =>
                {
                    if (request.RequestUri.AbsoluteUri == Gitee) return Response(payload);
                    if (request.RequestUri.AbsoluteUri != GitHub) throw new InvalidOperationException("Unexpected fixture URL.");
                    if (failure == "DNS")
                        throw new HttpRequestException("Fixture DNS failure", new SocketException((int)SocketError.HostNotFound));
                    if (failure == "transport") throw new HttpRequestException("Fixture transport failure");
                    if (failure == "HTTP 503") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException("The fixture header timeout did not cancel.");
                });
                if (failure == "response-header timeout") client.Timeout = TimeSpan.FromMilliseconds(100);
                await CheckSuccessfulDownload(check, client, root, package, payload, requests,
                    new[] { GitHub, Gitee }, $"GitHub {failure} falls back to the identical official Gitee package");
            }
            check(IsEmpty(root), "successful mirror fallback leaves no staging after disposal");

            var interruptedRequests = new List<string>();
            using (var client = Client(interruptedRequests, (request, _) => Task.FromResult(
                request.RequestUri.AbsoluteUri == GitHub
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedStream(payload, failOnRead: 3)) }
                    : Response(payload, fragmented: true))))
            {
                var progress = new ProgressRecorder();
                await CheckSuccessfulDownload(check, client, root, package, payload, interruptedRequests,
                    new[] { GitHub, Gitee }, "a source IOException restarts the mirror from byte zero without appending partial bytes", progress);
                check(progress.Values.Count > 2 && progress.Values.Count <= 101
                    && progress.Values[0] == 0 && progress.Values[^1] == 1
                    && progress.Values.Zip(progress.Values.Skip(1)).All(pair => pair.First <= pair.Second),
                    "mirror retry keeps progress monotonic, bounded and completes only after verification");
            }
            check(IsEmpty(root), "an interrupted primary leaves no partial staging after mirror disposal");

            // Break caught: accepting alternate bytes against anything other than the originally selected digest.
            var alternateMismatch = new List<string>();
            using (var client = Client(alternateMismatch, (request, _) => Task.FromResult(
                request.RequestUri.AbsoluteUri == GitHub ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Response(payload.Select(value => (byte)(value ^ 1)).ToArray()))))
            {
                var progress = new ProgressRecorder();
                bool refused = false;
                try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(package, progress); }
                catch (InvalidDataException) { refused = true; }
                check(refused && alternateMismatch.SequenceEqual(new[] { GitHub, Gitee })
                    && progress.Values.All(value => value < 1) && IsEmpty(root),
                    "alternate bytes must match the selected SHA256 and cannot report completion on integrity failure");
            }

            foreach (bool redirected in new[] { false, true })
            {
                var requests = new List<string>();
                using var client = Client(requests, (request, _) => Task.FromResult(
                    request.RequestUri.AbsoluteUri == Gitee && redirected
                        ? Redirect("/liangyun_Liu/HSRUnlocker/attach_files/123/download/" + FileName)
                        : request.RequestUri.AbsoluteUri == Attachment
                            ? Redirect(Cdn)
                            : Response(payload)));
                await CheckSuccessfulDownload(check, client, root, Package(payload, Gitee, UpdateSourcePreference.Gitee), payload, requests,
                    redirected ? new[] { Gitee, Attachment, Cdn } : new[] { Gitee },
                    redirected
                        ? "Gitee primary follows its official attachment and exact signed foruda CDN URL"
                        : "an official Gitee primary downloads and verifies without contacting GitHub");
            }
            check(IsEmpty(root), "Gitee direct and redirected downloads dispose their staging");

            var successRequests = new List<string>();
            using (var client = Client(successRequests, (_, _) => Task.FromResult(Response(payload))))
                await CheckSuccessfulDownload(check, client, root, package, payload, successRequests, new[] { GitHub },
                    "GitHub success verifies the original bytes without contacting the mirror");
            check(IsEmpty(root), "successful GitHub-only download disposes its staging");

            foreach (var sample in new (byte[] Bytes, long? DeclaredLength, string Hash, string Description)[]
            {
                (payload, payload.Length, new string('0', 64), "SHA256 mismatch"),
                (payload[..^1], null, package.Sha256, "truncated stream"),
                (payload.Concat(new byte[] { 7 }).ToArray(), null, package.Sha256, "oversized stream"),
                (payload, payload.Length + 1, package.Sha256, "Content-Length mismatch")
            })
            {
                var requests = new List<string>();
                using var client = Client(requests, (_, _) =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new FragmentedStream(sample.Bytes))
                    };
                    response.Content.Headers.ContentLength = sample.DeclaredLength;
                    return Task.FromResult(response);
                });
                bool refused = false;
                try
                {
                    using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(
                        new UpdatePackage("1.2.3", FileName, new Uri(GitHub), payload.Length, sample.Hash));
                }
                catch (InvalidDataException) { refused = true; }
                check(refused && requests.SequenceEqual(new[] { GitHub }) && IsEmpty(root),
                    $"{sample.Description} fails closed without mirror retry or leftover staging");
            }

            foreach (var sample in new (string Origin, string Target, string Description)[]
            {
                (GitHub, "http://github.com/L1angYun/HSRUnlocker/releases/download/v1.2.3/" + FileName, "HTTPS downgrade"),
                (GitHub, "https://attacker.invalid/" + FileName, "untrusted host"),
                (GitHub, "https://gitee.com.evil.invalid/" + FileName, "lookalike Gitee host"),
                (Gitee, "https://gitee.com/unrelated/repository/attach_files/123/download/" + FileName, "another Gitee repository"),
                (Gitee, "https://gitee.com/liangyun_liu/HSRUnlocker/attach_files/123/download/" + FileName, "changed owned repository casing"),
                (Gitee, "https://gitee.com/liangyun_Liu/HSRUnlocker/attach_files/123/download/hsrunlocker-1.2.3-win-x64-setup.exe", "lowercase owned attachment basename"),
                (Gitee, "https://gitee.com/liangyun_Liu/HSRUnlocker/releases/download/v1.2.3/hsrunlocker-1.2.3-win-x64-setup.exe", "lowercase release basename"),
                (Gitee, "https://foruda.gitee.com.evil.invalid/attach_file/456/" + FileName, "lookalike CDN host"),
                (Gitee, Cdn, "Gitee CDN bypass without an owned attachment"),
                (Gitee, LowercaseCdn, "lowercase Gitee CDN bypass without an owned attachment"),
                (Gitee, Attachment + "#fragment", "attachment fragment"),
                (Gitee, Attachment + "?query=unexpected", "attachment query"),
                (Gitee, "https://user@gitee.com/liangyun_Liu/HSRUnlocker/attach_files/123/download/" + FileName, "attachment userinfo"),
                (Gitee, "https://gitee.com:444/liangyun_Liu/HSRUnlocker/attach_files/123/download/" + FileName, "attachment non-443 port")
            })
            {
                var requests = new List<string>();
                using var client = Client(requests, (_, _) => Task.FromResult(Redirect(sample.Target)));
                bool refused = false;
                try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(Package(payload, sample.Origin)); }
                catch (InvalidDataException) { refused = true; }
                check(refused && requests.SequenceEqual(new[] { sample.Origin }) && IsEmpty(root),
                    $"redirect to {sample.Description} is rejected before requesting its target and never retried");
            }

            var automaticRequests = new List<string>();
            using (var client = Client(automaticRequests, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, Cdn)
            })))
            {
                bool refused = false;
                try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(package); }
                catch (InvalidDataException) { refused = true; }
                check(refused && automaticRequests.SequenceEqual(new[] { GitHub }) && IsEmpty(root),
                    "an automatically followed HTTPS response is rejected without accepting or retrying its bytes");
            }

            var loopingRequests = new List<string>();
            using (var client = Client(loopingRequests, (_, _) => Task.FromResult(Redirect(Attachment))))
            {
                bool refused = false;
                try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(Package(payload, Gitee)); }
                catch (InvalidDataException) { refused = true; }
                check(refused && loopingRequests.SequenceEqual(new[] { Gitee, Attachment })
                    && IsEmpty(root), "a Gitee attachment redirect loop is rejected before repeating the attachment request");
            }

            foreach (string moment in new[] { "before request", "during headers", "during streaming" })
            {
                var requests = new List<string>();
                using var cancellation = new CancellationTokenSource();
                using var client = Client(requests, async (_, token) =>
                {
                    if (moment == "during headers")
                    {
                        cancellation.Cancel();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new FragmentedStream(payload,
                            onSecondRead: moment == "during streaming" ? cancellation.Cancel : null))
                    };
                });
                if (moment == "before request") cancellation.Cancel();
                bool canceled = false;
                try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(package, null, cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                check(canceled && requests.SequenceEqual(moment == "before request" ? Array.Empty<string>() : new[] { GitHub })
                    && IsEmpty(root), $"caller cancellation {moment} propagates without mirror retry and removes partial staging");
            }

            var failedRequests = new List<string>();
            using (var client = Client(failedRequests, (_, _) => throw new HttpRequestException("Fixture unavailable")))
            {
                bool failed = false;
                try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(package); }
                catch (HttpRequestException) { failed = true; }
                check(failed && failedRequests.SequenceEqual(new[] { GitHub, Gitee }) && IsEmpty(root),
                    "network failure on both official sources ends after one mirror attempt and cleans all staging");
            }

            var signedRequests = new List<string>();
            using (var client = Client(signedRequests, (request, _) => Task.FromResult(
                request.RequestUri.AbsoluteUri == Gitee ? Redirect(Attachment)
                    : request.RequestUri.AbsoluteUri == Attachment ? Redirect(Cdn)
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
            {
                Exception failure = null;
                try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(Package(payload, Gitee, UpdateSourcePreference.Gitee)); }
                catch (HttpRequestException ex) { failure = ex; }
                check(failure != null && !failure.ToString().Contains("private-fixture-value", StringComparison.Ordinal)
                    && signedRequests.SequenceEqual(new[] { Gitee, Attachment, Cdn }) && IsEmpty(root),
                    "a signed CDN failure does not expose its query secret or retry the Gitee primary");
            }
        }
        finally
        {
            string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!Path.GetDirectoryName(Path.GetFullPath(root)).Equals(expectedParent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("HSRUnlocker-mirror-fixtures-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected mirror fixture cleanup directory.");
            Directory.Delete(root, true);
        }
    }

    private static async Task VerifyLowercaseCdnDownloadAsync(Action<bool, string> check, string root, byte[] payload)
    {
        // Break caught: rejecting the exact lowercase filename supplied by the owned attachment's CDN redirect.
        var requests = new List<string>();
        bool sourceClosed = false;
        using var client = Client(requests, (request, _) =>
        {
            string uri = request.RequestUri.AbsoluteUri;
            if (uri == Gitee) return Task.FromResult(Redirect(Attachment));
            if (uri == Attachment) return Task.FromResult(Redirect(LowercaseCdn));
            if (uri != LowercaseCdn) throw new InvalidOperationException("Unexpected lowercase CDN fixture URL.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FragmentedStream(payload, onDispose: () => sourceClosed = true))
            });
        });
        var progress = new ProgressRecorder();
        await CheckSuccessfulDownload(check, client, root, Package(payload, Gitee, UpdateSourcePreference.Gitee),
            payload, requests, new[] { Gitee, Attachment, LowercaseCdn },
            "Gitee-only follows the owned attachment to its exact lowercase CDN basename and verifies the canonical local installer",
            progress);
        check(sourceClosed && progress.Values[^1] == 1 && IsEmpty(root),
            "a lowercase CDN download closes its response stream and removes staging after disposal");
    }

    private static async Task VerifyLowercaseCdnIntegrityAsync(Action<bool, string> check, string root, byte[] payload)
    {
        // Break caught: accepting lowercase CDN paths bypasses selected integrity checks or enables an alternate-provider retry.
        foreach (var sample in new (byte[] Bytes, long? DeclaredLength, string Description)[]
        {
            (payload.Select(value => (byte)(value ^ 1)).ToArray(), payload.Length, "corrupted bytes"),
            (payload[..^1], null, "truncated stream"),
            (payload.Concat(new byte[] { 7 }).ToArray(), null, "oversized stream"),
            (payload, payload.Length + 1, "Content-Length mismatch")
        })
        {
            var requests = new List<string>();
            bool sourceClosed = false;
            using var client = Client(requests, (request, _) =>
            {
                string uri = request.RequestUri.AbsoluteUri;
                if (uri == Gitee) return Task.FromResult(Redirect(Attachment));
                if (uri == Attachment) return Task.FromResult(Redirect(LowercaseCdn));
                if (uri != LowercaseCdn) throw new InvalidOperationException("Unexpected lowercase CDN integrity fixture URL.");
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new FragmentedStream(sample.Bytes, onDispose: () => sourceClosed = true))
                };
                response.Content.Headers.ContentLength = sample.DeclaredLength;
                return Task.FromResult(response);
            });
            var progress = new ProgressRecorder();
            bool refused = false;
            try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(Package(payload, Gitee), progress); }
            catch (InvalidDataException) { refused = true; }
            check(refused && sourceClosed && requests.SequenceEqual(new[] { Gitee, Attachment, LowercaseCdn })
                && progress.Values.All(value => value < 1) && IsEmpty(root),
                $"lowercase CDN {sample.Description} fails closed without alternate retry, completion or leftover staging");
        }
    }

    private static async Task VerifyCdnRedirectBoundariesAsync(Action<bool, string> check, string root, byte[] payload)
    {
        // Break caught: a broad case-insensitive basename rule or relaxed terminal URL validation accepts an untrusted redirect.
        foreach (var sample in new (string Target, string Description)[]
        {
            ("https://foruda.gitee.com/attach_file/456/hsrUnlocker-1.2.3-win-x64-setup.exe", "other mixed casing"),
            ("https://foruda.gitee.com/attach_file/456/hsrunlocker-1.2.4-win-x64-setup.exe", "changed version"),
            ("https://foruda.gitee.com/attach_file/456/hsrunlocker-1.2.3-win-x64-setup.zip", "changed extension"),
            ("https://foruda.gitee.com/attach_file/456/other-1.2.3-win-x64-setup.exe", "changed product name"),
            ("https://foruda.gitee.com.evil.invalid/attach_file/456/hsrunlocker-1.2.3-win-x64-setup.exe", "lookalike CDN host"),
            ("https://attacker.invalid/attach_file/456/hsrunlocker-1.2.3-win-x64-setup.exe", "foreign CDN host"),
            ("http://foruda.gitee.com/attach_file/456/hsrunlocker-1.2.3-win-x64-setup.exe", "HTTP downgrade"),
            ("https://foruda.gitee.com:444/attach_file/456/hsrunlocker-1.2.3-win-x64-setup.exe", "non-443 port"),
            ("https://user@foruda.gitee.com/attach_file/456/hsrunlocker-1.2.3-win-x64-setup.exe", "userinfo"),
            ("https://foruda.gitee.com/attach_file/456/hsrunlocker-1.2.3-win-x64-setup.exe#fragment", "fragment"),
            ("https://foruda.gitee.com/attach_file/not-numeric/hsrunlocker-1.2.3-win-x64-setup.exe", "nonnumeric attachment identifier"),
            ("https://foruda.gitee.com/attach_file/456/download/hsrunlocker-1.2.3-win-x64-setup.exe", "extra path segment")
        })
        {
            var requests = new List<string>();
            using var client = Client(requests, (request, _) => Task.FromResult(
                request.RequestUri.AbsoluteUri == Gitee ? Redirect(Attachment)
                    : request.RequestUri.AbsoluteUri == Attachment ? Redirect(sample.Target)
                    : throw new InvalidOperationException("An unsafe CDN target was requested.")));
            bool refused = false;
            try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(Package(payload, Gitee)); }
            catch (InvalidDataException) { refused = true; }
            check(refused && requests.SequenceEqual(new[] { Gitee, Attachment }) && IsEmpty(root),
                $"terminal CDN redirect with {sample.Description} is rejected before its target without retry or leftover staging");
        }

        var onwardRequests = new List<string>();
        using var onwardClient = Client(onwardRequests, (request, _) => Task.FromResult(
            request.RequestUri.AbsoluteUri == Gitee ? Redirect(Attachment)
                : request.RequestUri.AbsoluteUri == Attachment ? Redirect(LowercaseCdn)
                : Redirect(Cdn)));
        bool onwardRefused = false;
        try { using StagedUpdate staged = await new UpdateInstaller(onwardClient, root).DownloadAsync(Package(payload, Gitee)); }
        catch (InvalidDataException) { onwardRefused = true; }
        check(onwardRefused && onwardRequests.SequenceEqual(new[] { Gitee, Attachment, LowercaseCdn }) && IsEmpty(root),
            "a lowercase terminal CDN response cannot redirect onward or retry another provider");
    }

    private static async Task VerifyProtocolLengthFailureAsync(Action<bool, string> check, string root, byte[] payload)
    {
        // Break caught: explicit HTTP premature EOF bypasses total-length validation and retries a different provider.
        foreach (string failureKind in new[] { "HttpIOException stream", "HttpRequestException stream", "HttpRequestException headers" })
        {
            var requests = new List<string>();
            using var client = Client(requests, (request, _) =>
            {
                string uri = request.RequestUri.AbsoluteUri;
                if (uri == Gitee) return Task.FromResult(Redirect(Attachment));
                if (uri == Attachment) return Task.FromResult(Redirect(Cdn));
                if (uri != Cdn) return Task.FromResult(Response(payload));
                if (failureKind == "HttpRequestException headers") throw ResponseEnded(true);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ProtocolEndedStream(payload, failureKind == "HttpRequestException stream"))
                };
                response.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(response);
            });
            var progress = new ProgressRecorder();
            Exception failure = null;
            try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(Package(payload, Gitee), progress); }
            catch (Exception error) { failure = error; }
            check(failure is InvalidDataException && requests.SequenceEqual(new[] { Gitee, Attachment, Cdn })
                && progress.Values.All(value => value < 1) && IsEmpty(root),
                $"explicit ResponseEnded in {failureKind} fails closed without alternate retry, completion or partial staging");
            check(!failure.ToString().Contains("private-fixture-value", StringComparison.Ordinal),
                "protocol-length failure diagnostics never expose a signed CDN query or its inner exception");
        }
    }

    private static Exception ResponseEnded(bool requestException)
    {
        string message = "Fixture response ended prematurely at " + Cdn;
        var inner = new IOException("Fixture inner error at " + Cdn);
        return requestException
            ? new HttpRequestException(HttpRequestError.ResponseEnded, message, inner, null)
            : new HttpIOException(HttpRequestError.ResponseEnded, message, inner);
    }

    private static async Task CheckSuccessfulDownload(Action<bool, string> check, HttpClient client, string root,
        UpdatePackage package, byte[] payload, List<string> requests, string[] expectedRequests, string description,
        IProgress<double> progress = null, CancellationToken cancellationToken = default)
    {
        StagedUpdate staged = null;
        Exception failure = null;
        try { staged = await new UpdateInstaller(client, root).DownloadAsync(package, progress, cancellationToken); }
        catch (Exception ex) { failure = ex; }
        using (staged)
        {
            byte[] actual = staged == null ? null : File.ReadAllBytes(staged.FilePath);
            check(failure == null && staged != null && actual.SequenceEqual(payload)
                && actual.LongLength == package.Size
                && Convert.ToHexString(SHA256.HashData(actual)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(staged.FilePath) == FileName
                && !File.Exists(Path.Combine(Path.GetDirectoryName(staged.FilePath), "download.bin"))
                && requests.SequenceEqual(expectedRequests),
                description + (failure == null ? "" : " (received " + failure.GetType().Name + ")"));
        }
    }

    private static UpdatePackage Package(byte[] bytes, string url, UpdateSourcePreference preference = UpdateSourcePreference.Auto) =>
        new("1.2.3", FileName, new Uri(url),
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), preference);

    private static bool IsEmpty(string root) => !Directory.EnumerateFileSystemEntries(root).Any();

    private static HttpResponseMessage Response(byte[] bytes, bool fragmented = false) => new(HttpStatusCode.OK)
    {
        Content = fragmented ? new StreamContent(new FragmentedStream(bytes)) : new ByteArrayContent(bytes)
    };

    private static HttpResponseMessage Redirect(string target)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpClient Client(List<string> requests,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new FixtureHandler(requests, respond));

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly List<string> requests;
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;
        public FixtureHandler(List<string> requests, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            this.requests = requests;
            this.respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request.RequestUri.AbsoluteUri);
            return respond(request, cancellationToken);
        }
    }

    private sealed class FragmentedStream : MemoryStream
    {
        private readonly int failOnRead;
        private readonly Action onSecondRead;
        private readonly Action onDispose;
        private int reads;
        public FragmentedStream(byte[] bytes, int failOnRead = 0, Action onSecondRead = null, Action onDispose = null) : base(bytes)
        {
            this.failOnRead = failOnRead;
            this.onSecondRead = onSecondRead;
            this.onDispose = onDispose;
        }

        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++reads == 2) onSecondRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (reads == failOnRead) throw new IOException("Fixture connection interrupted after partial bytes.");
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 1024)], cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) onDispose?.Invoke();
        }
    }

    private sealed class StalledStream : MemoryStream
    {
        private readonly Action closed;
        private int reads;
        internal StalledStream(byte[] bytes, Action closed) : base(bytes) => this.closed = closed;
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++reads == 1) return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 1024)], cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) closed();
        }
    }

    private sealed class ProtocolEndedStream : MemoryStream
    {
        private readonly bool requestException;
        private int reads;
        internal ProtocolEndedStream(byte[] bytes, bool requestException) : base(bytes) => this.requestException = requestException;
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++reads > 1) throw ResponseEnded(requestException);
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 1024)], cancellationToken);
        }
    }

    private sealed class ProgressRecorder : IProgress<double>
    {
        public List<double> Values { get; } = new();
        public void Report(double value) => Values.Add(value);
    }
}
