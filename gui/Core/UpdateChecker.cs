using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StarRailUnlocker.Core
{
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
        private static readonly Regex StableVersion = new(
            @"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*))?\z",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        private readonly HttpClient client;

        public UpdateChecker(HttpClient client) => this.client = client ?? throw new ArgumentNullException(nameof(client));

        public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryVersion(currentVersion, out Version current))
                throw new ArgumentException("The current version must be a stable numeric version.", nameof(currentVersion));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            CancellationToken token = timeout.Token;
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/L1angYun/HSRUnlocker/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("HSRUnlocker-UpdateChecker/1.1");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
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
