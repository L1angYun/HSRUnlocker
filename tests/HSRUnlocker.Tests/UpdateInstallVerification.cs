using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using StarRailUnlocker.Core;

internal static class UpdateInstallVerification
{
    public static void Run(Action<bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "HSRUnlocker-update-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        byte[] payload = Enumerable.Range(0, 200000).Select(value => (byte)(value % 251)).ToArray();
        UpdatePackage package = Package(payload);
        try
        {
            int calls = 0;
            using (var client = Client((request, _) =>
            {
                calls++;
                check(request.Method == HttpMethod.Get && request.RequestUri == package.DownloadUri,
                    "installer download requests the validated official asset URL");
                check(request.Headers.Authorization == null, "installer download sends no authorization credentials");
                return Task.FromResult(Response(new ChunkedStream(payload)));
            }))
            {
                var progress = new ProgressRecorder();
                var installer = new UpdateInstaller(client, root);
                using (StagedUpdate staged = await installer.DownloadAsync(package, progress))
                {
                    check(File.ReadAllBytes(staged.FilePath).SequenceEqual(payload),
                        "downloaded installer contains exactly the fixture's bytes");
                    check(Path.GetFileName(staged.FilePath) == "HSRUnlocker-1.2.3-win-x64-Setup.exe"
                        && !File.Exists(Path.Combine(Path.GetDirectoryName(staged.FilePath), "download.bin")),
                        "verified download is renamed from its private partial filename");
                    check(progress.Values.Count <= 101 && progress.Values.Count > 1
                        && progress.Values[0] == 0 && progress.Values[^1] == 1
                        && progress.Values.Zip(progress.Values.Skip(1)).All(pair => pair.First <= pair.Second),
                        "download progress increases from zero to one with at most one callback per percent");
                    string injectedText = "Fixture quote ' \" $([System.IO.File]::Delete('never')) \n translated error";
                    System.Diagnostics.ProcessStartInfo start = UpdateInstallHelper.StartInfo(staged, 12345, 67890, injectedText);
                    check(start.FileName == Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
                        && start.WorkingDirectory == Environment.SystemDirectory && !start.UseShellExecute
                        && start.CreateNoWindow && start.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden
                        && start.RedirectStandardOutput,
                        "handoff targets hidden system Windows PowerShell with an isolated working directory and readiness pipe");
                    check(start.ArgumentList.Count == 5 && start.ArgumentList.Take(4).SequenceEqual(
                        new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand" })
                        && !System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList[4])).Contains(injectedText),
                        "handoff uses a fixed encoded command without interpolating translated or path values");
                    check(start.Environment["HSR_UPD_INSTALLER"] == staged.FilePath
                        && start.Environment["HSR_UPD_PARENT_ID"] == "12345"
                        && start.Environment["HSR_UPD_PARENT_START"] == "67890"
                        && start.Environment["HSR_UPD_SIZE"] == "200000"
                        && start.Environment["HSR_UPD_SHA256"] == package.Sha256
                        && start.Environment["HSR_UPD_ERROR"] == injectedText
                        && start.Environment["PSModulePath"] == "",
                        "handoff passes process identity, verified package and localized errors as environment data");
                    bool refused = false;
                    try { using PreparedUpdateInstall prepared = await installer.PrepareInstallAsync(staged, "Fixture error"); }
                    catch (InvalidOperationException) { refused = true; }
                    check(refused, "fixture staging can never start an installer handoff helper");
                }
                check(!Directory.EnumerateFileSystemEntries(root).Any(), "disposing an unhanded staged update removes only its own download");
                using HttpResponseMessage reusable = await client.GetAsync(package.DownloadUri);
                check(reusable.IsSuccessStatusCode && calls == 2, "downloader preserves its caller-owned HttpClient");
            }

            using (var client = Client((_, _) => Task.FromResult(Response(new ChunkedStream(payload)))))
            {
                StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(package);
                bool disposedWithoutError = false;
                using (var held = new FileStream(staged.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    staged.Dispose();
                    disposedWithoutError = true;
                    check(File.Exists(staged.FilePath), "a locked aborted fixture remains present rather than being replaced or recursively deleted");
                }
                staged.DeleteFiles();
                check(disposedWithoutError && !Directory.EnumerateFileSystemEntries(root).Any(),
                    "best-effort abort cleanup never interrupts caller recovery when a staged file is temporarily locked");
            }

            foreach (var sample in new (byte[] Bytes, long DeclaredLength, string Hash, string Description)[]
            {
                (payload, payload.Length, new string('0', 64), "mismatched SHA256"),
                (payload[..^1], -1, package.Sha256, "truncated stream without a Content-Length"),
                (payload.Concat(new byte[] { 7 }).ToArray(), -1, package.Sha256, "stream larger than the asset's exact size"),
                (payload, payload.Length + 1, package.Sha256, "inconsistent declared Content-Length")
            })
            {
                using var client = Client((_, _) =>
                {
                    var response = Response(new ChunkedStream(sample.Bytes));
                    if (sample.DeclaredLength >= 0) response.Content.Headers.ContentLength = sample.DeclaredLength;
                    return Task.FromResult(response);
                });
                UpdatePackage invalid = new(package.Version, package.FileName, package.DownloadUri, package.Size, sample.Hash);
                bool refused = false;
                try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(invalid); }
                catch (InvalidDataException) { refused = true; }
                check(refused, $"installer download rejects {sample.Description}");
                check(!Directory.EnumerateFileSystemEntries(root).Any(), $"failed {sample.Description} leaves no partial staging");
            }

            using (var cancellation = new CancellationTokenSource())
            using (var client = Client((_, _) => Task.FromResult(Response(new ChunkedStream(payload, () => cancellation.Cancel())))))
            {
                bool canceled = false;
                try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(package, null, cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                check(canceled && !Directory.EnumerateFileSystemEntries(root).Any(),
                    "cancellation during streamed download propagates and removes the partial file and folder");
            }

            using (var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))))
            {
                bool refused = false;
                try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(package); }
                catch (HttpRequestException ex) { refused = ex.StatusCode == HttpStatusCode.Forbidden; }
                check(refused && !Directory.EnumerateFileSystemEntries(root).Any(),
                    "installer HTTP failures preserve their status and clean staging");
            }

            using (var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://attacker.invalid/setup.exe")
            })))
            {
                bool refused = false;
                try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(package); }
                catch (InvalidDataException) { refused = true; }
                check(refused && !Directory.EnumerateFileSystemEntries(root).Any(),
                    "an insecure final download URI is rejected before staging is retained");
            }

            using (var client = Client((_, _) => throw new InvalidOperationException("Invalid package must not send")))
            {
                foreach (UpdatePackage invalid in new[]
                {
                    new UpdatePackage(package.Version, "../evil.exe", package.DownloadUri, package.Size, package.Sha256),
                    new UpdatePackage(package.Version, package.FileName, new Uri("https://attacker.invalid/setup.exe"), package.Size, package.Sha256),
                    new UpdatePackage(package.Version, package.FileName, package.DownloadUri, 0, package.Sha256),
                    new UpdatePackage(package.Version, package.FileName, package.DownloadUri, package.Size, "invalid")
                })
                {
                    bool refused = false;
                    try { using StagedUpdate staged = await new UpdateInstaller(client, root).DownloadAsync(invalid); }
                    catch (ArgumentException) { refused = true; }
                    check(refused && !Directory.EnumerateFileSystemEntries(root).Any(),
                        "invalid installer package is rejected before filesystem or HTTP side effects");
                }
            }
        }
        finally
        {
            string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!Path.GetDirectoryName(Path.GetFullPath(root)).Equals(expectedParent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("HSRUnlocker-update-fixtures-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture cleanup directory.");
            Directory.Delete(root, true);
        }
    }

    private static UpdatePackage Package(byte[] bytes) => new("1.2.3", "HSRUnlocker-1.2.3-win-x64-Setup.exe",
        new Uri("https://github.com/L1angYun/HSRUnlocker/releases/download/v1.2.3/HSRUnlocker-1.2.3-win-x64-Setup.exe"),
        bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private static HttpResponseMessage Response(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new FixtureHandler(respond));

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;
        public FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => this.respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class ChunkedStream : MemoryStream
    {
        private readonly Action cancel;
        private int reads;
        public ChunkedStream(byte[] bytes, Action cancel = null) : base(bytes) => this.cancel = cancel;
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++reads == 2) cancel?.Invoke();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 1024)], cancellationToken);
        }
    }

    private sealed class ProgressRecorder : IProgress<double>
    {
        public List<double> Values { get; } = new();
        public void Report(double value) => Values.Add(value);
    }
}
