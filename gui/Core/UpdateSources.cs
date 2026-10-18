using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace StarRailUnlocker.Core
{
    internal static class UpdateSources
    {
        internal const string GitHubRoot = "https://github.com/L1angYun/HSRUnlocker";
        internal const string GiteeRoot = "https://gitee.com/liangyun_Liu/HSRUnlocker";
        internal static readonly Uri GiteeReleases = new("https://gitee.com/api/v5/repos/liangyun_Liu/HSRUnlocker/releases?direction=desc&per_page=100");

        internal static Uri FileUri(string tag, string fileName, bool gitee) =>
            new((gitee ? GiteeRoot : GitHubRoot) + "/releases/download/" + tag + "/" + fileName);

        internal static Uri[] DownloadLocations(UpdatePackage package)
        {
            if (!Enum.IsDefined(package.Preference))
                throw new ArgumentException("The installer source preference is invalid.", nameof(package));
            foreach (string tag in new[] { "v" + package.Version, package.Version })
            {
                Uri github = FileUri(tag, package.FileName, false);
                Uri gitee = FileUri(tag, package.FileName, true);
                if (package.DownloadUri.AbsoluteUri == github.AbsoluteUri && package.Preference != UpdateSourcePreference.Gitee)
                    return package.Preference == UpdateSourcePreference.Auto ? new[] { github, gitee } : new[] { github };
                if (package.DownloadUri.AbsoluteUri == gitee.AbsoluteUri && package.Preference != UpdateSourcePreference.GitHub)
                    return package.Preference == UpdateSourcePreference.Auto ? new[] { gitee, github } : new[] { gitee };
            }
            throw new ArgumentException("The installer package does not use an official release URL.", nameof(package));
        }

        internal static bool IsTransportFailure(Exception error) =>
            error is not InvalidDataException
            && error is not UpdateRateLimitException { InnerException: InvalidDataException }
            && !IsProtocolLengthFailure(error)
            && (error is HttpRequestException || error is IOException || error is OperationCanceledException);

        internal static bool IsProtocolLengthFailure(Exception error) =>
            error is HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded }
                or HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded };

        internal static Exception SafeFailure(Exception error) => error switch
        {
            UpdateRateLimitException => error,
            InvalidDataException or UriFormatException => new InvalidDataException("The update metadata or redirect is invalid or unsafe."),
            HttpRequestException http => new HttpRequestException("The update server request failed.", null, http.StatusCode),
            OperationCanceledException => new OperationCanceledException("The update server request was canceled or timed out."),
            _ => new IOException("The update server response could not be read.")
        };

        internal static void ValidateResponse(HttpResponseMessage response, Uri requested)
        {
            Uri actual = response.RequestMessage?.RequestUri;
            if (actual != null && (!actual.IsAbsoluteUri || actual.AbsoluteUri != requested.AbsoluteUri))
                throw new InvalidDataException("The update response followed an unexpected URI.");
        }

        internal static async Task<HttpResponseMessage> GetFileAsync(HttpClient client, Uri initial,
            string fileName, bool gitee, CancellationToken token, string expectedTag = null)
        {
            Uri uri = initial;
            FileLocation previous = FileLocation.Release;
            for (int redirects = 0; ; redirects++)
            {
                FileLocation location = ValidateFileUri(uri, fileName, gitee, expectedTag);
                if ((redirects == 0 && location != FileLocation.Release)
                    || (gitee && redirects > 0
                        && !((previous == FileLocation.Release && location == FileLocation.Attachment)
                            || (previous == FileLocation.Attachment && location == FileLocation.Cdn))))
                    throw new InvalidDataException("The update redirect transition is not trusted.");
                previous = location;
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("HSRUnlocker-Updater/1.3");
                using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                headersTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                HttpResponseMessage response;
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (HttpRequestException error)
                {
                    // A transport error may contain the CDN's expiring signed query.
                    throw new HttpRequestException(error.HttpRequestError, "The update server request failed.", null, error.StatusCode);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new OperationCanceledException("The update server request timed out.");
                }
                try
                {
                    ValidateResponse(response, uri);
                    if (!IsRedirect(response.StatusCode))
                    {
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException("The update server returned an unsuccessful response.", null, response.StatusCode);
                        return response;
                    }
                    if (redirects >= 5 || response.Headers.Location == null)
                        throw new InvalidDataException("The update redirect limit or location is invalid.");
                    try
                    {
                        uri = response.Headers.Location.IsAbsoluteUri
                            ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                    }
                    catch (UriFormatException)
                    {
                        throw new InvalidDataException("The update redirect location is invalid.");
                    }
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
                response.Dispose();
            }
        }

        private static bool IsRedirect(HttpStatusCode status) =>
            status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

        private enum FileLocation { Release, Attachment, Cdn }

        private static FileLocation ValidateFileUri(Uri uri, string fileName, bool gitee, string expectedTag)
        {
            if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443
                || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("The update location is not a trusted HTTPS endpoint.");
            if (!gitee && uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return FileLocation.Cdn;
            if (gitee && uri.Host.Equals("foruda.gitee.com", StringComparison.OrdinalIgnoreCase)
                && AttachmentPath(uri.AbsolutePath, "/attach_file/", fileName, false)) return FileLocation.Cdn;

            string host = gitee ? "gitee.com" : "github.com";
            if (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && uri.Query.Length == 0)
            {
                string repository = gitee ? "/liangyun_Liu/HSRUnlocker" : "/L1angYun/HSRUnlocker";
                if (!gitee && expectedTag == null && fileName == "update.json"
                    && uri.AbsolutePath == repository + "/releases/latest/download/update.json") return FileLocation.Release;
                string prefix = repository + "/releases/download/";
                if (uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    string[] parts = uri.AbsolutePath.Substring(prefix.Length).Split('/');
                    if (parts.Length == 2 && parts[1] == fileName
                        && UpdateChecker.TryVersion(parts[0], out _)
                        && (expectedTag == null || parts[0] == expectedTag)) return FileLocation.Release;
                }
                if (gitee && AttachmentPath(uri.AbsolutePath, repository + "/attach_files/", fileName, true)) return FileLocation.Attachment;
            }
            throw new InvalidDataException("The update location is outside the trusted repository endpoints.");
        }

        private static bool AttachmentPath(string path, string prefix, string fileName, bool downloadSegment)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string[] parts = path.Substring(prefix.Length).Split('/');
            return parts.Length == (downloadSegment ? 3 : 2)
                && parts[0].Length > 0 && ulong.TryParse(parts[0], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out _)
                && (!downloadSegment || parts[1] == "download")
                && (parts[^1] == fileName
                    || (!downloadSegment && parts[^1] == fileName.ToLowerInvariant()));
        }
    }
}
