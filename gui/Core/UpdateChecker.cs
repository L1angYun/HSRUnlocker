using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StarRailUnlocker.Core
{
    public sealed class UpdateRateLimitException : HttpRequestException
    {
        public DateTimeOffset RetryAt { get; }

        internal UpdateRateLimitException(DateTimeOffset retryAt, HttpStatusCode status, Exception fallbackFailure)
            : base("GitHub API quota is exhausted and first-party release metadata is unavailable.", fallbackFailure, status) =>
            RetryAt = retryAt;
    }

    public sealed class UpdateCheckResult
    {
        public string Version { get; }
        public bool IsNewer { get; }
        public Uri ReleaseUri { get; }
        public UpdatePackage Installer { get; }

        internal UpdateCheckResult(string version, bool isNewer, Uri releaseUri, UpdatePackage installer = null)
        {
            Version = version;
            IsNewer = isNewer;
            ReleaseUri = releaseUri;
            Installer = installer;
        }
    }

    public sealed class UpdatePackage
    {
        public string Version { get; }
        public string FileName { get; }
        public Uri DownloadUri { get; }
        public long Size { get; }
        public string Sha256 { get; }

        internal UpdatePackage(string version, string fileName, Uri downloadUri, long size, string sha256)
        {
            Version = version;
            FileName = fileName;
            DownloadUri = downloadUri;
            Size = size;
            Sha256 = sha256;
        }
    }

    public sealed class UpdateChecker
    {
        private const int MaximumResponseBytes = 256 * 1024;
        private static readonly Uri ApiUri = new("https://api.github.com/repos/L1angYun/HSRUnlocker/releases/latest");
        private static readonly Uri ManifestUri = new("https://github.com/L1angYun/HSRUnlocker/releases/latest/download/update.json");
        private static readonly Regex StableVersion = new(
            @"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*))?\z",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        private readonly HttpClient client;
        private readonly Func<DateTimeOffset> clock;
        private RateLimitState rateLimit;

        public UpdateChecker(HttpClient client) : this(client, () => DateTimeOffset.UtcNow) { }

        internal UpdateChecker(HttpClient client, Func<DateTimeOffset> clock)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryVersion(currentVersion, out Version current))
                throw new ArgumentException("The current version must be a stable numeric version.", nameof(currentVersion));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            CancellationToken token = timeout.Token;
            RateLimitState limited = Volatile.Read(ref rateLimit);
            if (limited != null && limited.RetryAt > clock())
                return await ReadFallbackAsync(current, limited, cancellationToken, token).ConfigureAwait(false);

            using (var request = MetadataRequest(ApiUri, true))
            using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false))
            {
                ValidateResponseUri(response, ApiUri);
                limited = ReadRateLimit(response, clock());
                if (limited == null)
                {
                    response.EnsureSuccessStatusCode();
                    return await ReadReleaseAsync(response, current, token).ConfigureAwait(false);
                }
                Volatile.Write(ref rateLimit, limited);
            }
            return await ReadFallbackAsync(current, limited, cancellationToken, token).ConfigureAwait(false);
        }

        private async Task<UpdateCheckResult> ReadFallbackAsync(Version current, RateLimitState limited,
            CancellationToken callerToken, CancellationToken token)
        {
            try
            {
                Uri uri = ManifestUri;
                for (int redirects = 0; ; redirects++)
                {
                    ValidateManifestUri(uri);
                    using var request = MetadataRequest(uri, false);
                    using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);
                    ValidateResponseUri(response, uri);
                    if (response.StatusCode == HttpStatusCode.MovedPermanently || response.StatusCode == HttpStatusCode.Found
                        || response.StatusCode == HttpStatusCode.SeeOther || response.StatusCode == HttpStatusCode.TemporaryRedirect
                        || response.StatusCode == HttpStatusCode.PermanentRedirect)
                    {
                        if (redirects >= 5 || response.Headers.Location == null)
                            throw new InvalidDataException("The release metadata redirect limit or location is invalid.");
                        uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    return await ReadReleaseAsync(response, current, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is InvalidDataException
                || ex is OperationCanceledException || ex is UriFormatException)
            {
                callerToken.ThrowIfCancellationRequested();
                // HttpClient failures can echo a signed CDN URL. Expose only safe, useful diagnostics.
                Exception diagnostic = ex is HttpRequestException http
                    ? new HttpRequestException("The first-party release metadata request failed.", null, http.StatusCode)
                    : ex is OperationCanceledException
                        ? new TimeoutException("The first-party release metadata request timed out.")
                        : ex is InvalidDataException || ex is UriFormatException
                            ? new InvalidDataException("The first-party release metadata is invalid or unsafe.")
                            : new IOException("The first-party release metadata could not be read.");
                throw new UpdateRateLimitException(limited.RetryAt, limited.Status, diagnostic);
            }
        }

        private static HttpRequestMessage MetadataRequest(Uri uri, bool api)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(api ? "application/vnd.github+json" : "application/json");
            request.Headers.UserAgent.ParseAdd("HSRUnlocker-UpdateChecker/1.1");
            if (api) request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            return request;
        }

        private static void ValidateResponseUri(HttpResponseMessage response, Uri requested)
        {
            Uri actual = response.RequestMessage?.RequestUri;
            if (actual != null && (!actual.IsAbsoluteUri || actual.AbsoluteUri != requested.AbsoluteUri))
                throw new InvalidDataException("The release metadata response followed an unexpected URI.");
        }

        private static void ValidateManifestUri(Uri uri)
        {
            if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443
                || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("The release metadata location is not a trusted HTTPS endpoint.");
            if (uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return;
            if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.Query.Length == 0)
            {
                if (uri.AbsolutePath == "/L1angYun/HSRUnlocker/releases/latest/download/update.json") return;
                const string prefix = "/L1angYun/HSRUnlocker/releases/download/";
                const string suffix = "/update.json";
                string path = uri.AbsolutePath;
                if (path.Length > prefix.Length + suffix.Length
                    && path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal)
                    && TryVersion(path.Substring(prefix.Length, path.Length - prefix.Length - suffix.Length), out _)) return;
            }
            throw new InvalidDataException("The release metadata location is outside the trusted repository endpoints.");
        }

        private static RateLimitState ReadRateLimit(HttpResponseMessage response, DateTimeOffset now)
        {
            bool exhausted = HeaderValue(response, "X-RateLimit-Remaining")?.Trim() == "0";
            if (response.StatusCode != HttpStatusCode.TooManyRequests
                && (response.StatusCode != HttpStatusCode.Forbidden || (!exhausted && !response.Headers.Contains("Retry-After"))))
                return null;
            DateTimeOffset? retry = null;
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter?.Delta is TimeSpan delta)
                retry = now + (delta > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : delta);
            else if (retryAfter?.Date is DateTimeOffset date) retry = date;
            if (long.TryParse(HeaderValue(response, "X-RateLimit-Reset"), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
                && seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            {
                DateTimeOffset reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (retry == null || reset > retry.Value) retry = reset;
            }
            DateTimeOffset deadline = retry ?? now.AddMinutes(1);
            if (deadline < now.AddSeconds(1)) deadline = now.AddSeconds(1);
            if (deadline > now.AddHours(24)) deadline = now.AddHours(24);
            return new RateLimitState(deadline, response.StatusCode);
        }

        private static string HeaderValue(HttpResponseMessage response, string name)
        {
            if (response.Headers.TryGetValues(name, out var values))
                foreach (string value in values) return value;
            return null;
        }

        private sealed class RateLimitState
        {
            internal DateTimeOffset RetryAt { get; }
            internal HttpStatusCode Status { get; }
            internal RateLimitState(DateTimeOffset retryAt, HttpStatusCode status)
            {
                RetryAt = retryAt;
                Status = status;
            }
        }

        private static async Task<UpdateCheckResult> ReadReleaseAsync(HttpResponseMessage response, Version current, CancellationToken token)
        {
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new InvalidDataException("The release response exceeds the allowed size.");

            using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var body = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == 0) break;
                if (body.Length + count > MaximumResponseBytes)
                    throw new InvalidDataException("The release response exceeds the allowed size.");
                body.Write(buffer, 0, count);
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(body.ToArray());
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("draft", out JsonElement draft) || draft.ValueKind != JsonValueKind.False
                    || !root.TryGetProperty("prerelease", out JsonElement prerelease) || prerelease.ValueKind != JsonValueKind.False
                    || !root.TryGetProperty("tag_name", out JsonElement tagValue) || tagValue.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("The release response does not describe a stable release.");

                string tag = tagValue.GetString();
                if (!TryVersion(tag, out Version latest))
                    throw new InvalidDataException("The release tag is not a stable numeric version.");
                token.ThrowIfCancellationRequested();
                string displayVersion = tag[0] == 'v' ? tag.Substring(1) : tag;
                UpdatePackage installer = ReadInstaller(root, tag, displayVersion);
                return new UpdateCheckResult(displayVersion,
                    latest > current, new Uri("https://github.com/L1angYun/HSRUnlocker/releases/tag/" + tag), installer);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The release response is not valid JSON.", ex);
            }
        }

        private static UpdatePackage ReadInstaller(JsonElement root, string tag, string version)
        {
            if (!root.TryGetProperty("assets", out JsonElement assets)) return null;
            if (assets.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The release asset collection is invalid.");
            string fileName = "HSRUnlocker-" + version + "-win-x64-Setup.exe";
            UpdatePackage installer = null;
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object
                    || !asset.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String
                    || name.GetString() != fileName) continue;
                if (installer != null
                    || !asset.TryGetProperty("size", out JsonElement size) || size.ValueKind != JsonValueKind.Number
                    || !size.TryGetInt64(out long bytes) || bytes < 1 || bytes > 512L * 1024 * 1024
                    || !asset.TryGetProperty("digest", out JsonElement digest) || digest.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("The installer size or integrity metadata is invalid.");
                string hash = digest.GetString();
                if (hash == null || !Regex.IsMatch(hash, @"\Asha256:[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant))
                    throw new InvalidDataException("The installer requires a valid SHA256 digest.");
                installer = new UpdatePackage(version, fileName,
                    new Uri("https://github.com/L1angYun/HSRUnlocker/releases/download/" + tag + "/" + fileName),
                    bytes, hash.Substring(7).ToLowerInvariant());
            }
            return installer;
        }

        private static bool TryVersion(string text, out Version version)
        {
            version = null;
            if (text == null || text.Length > 64) return false;
            Match match = StableVersion.Match(text);
            if (!match.Success) return false;
            int[] parts = new int[4];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!match.Groups[i + 1].Success) continue;
                if (!int.TryParse(match.Groups[i + 1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
                    return false;
            }
            version = new Version(parts[0], parts[1], parts[2], parts[3]);
            return true;
        }
    }
}
