using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace StarRailUnlocker.Core
{
    public sealed class StagedUpdate : IDisposable
    {
        public UpdatePackage Package { get; }
        public string FilePath { get; }
        internal string DirectoryPath { get; }
        internal bool IsProtected { get; }
        private SafeFileHandle directoryHandle;
        private bool handedOff;
        private bool disposed;

        internal StagedUpdate(UpdatePackage package, string directory, bool isProtected, SafeFileHandle handle)
        {
            Package = package;
            DirectoryPath = directory;
            FilePath = Path.Combine(directory, package.FileName);
            IsProtected = isProtected;
            directoryHandle = handle;
        }

        internal void EnsureAvailable()
        {
            if (disposed || handedOff) throw new InvalidOperationException("The staged update is no longer available.");
        }

        internal void HandOff()
        {
            EnsureAvailable();
            handedOff = true;
            directoryHandle?.Dispose();
            directoryHandle = null;
        }

        internal void DeleteFiles()
        {
            directoryHandle?.Dispose();
            directoryHandle = null;
            // Delete only the two filenames this staging operation owns; never recurse.
            if (!Directory.Exists(DirectoryPath)) return;
            UpdateInstaller.RejectReparseAncestors(DirectoryPath);
            File.Delete(Path.Combine(DirectoryPath, "download.bin"));
            File.Delete(FilePath);
            Directory.Delete(DirectoryPath);
        }

        internal void TryDeleteFiles()
        {
            try { DeleteFiles(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (!handedOff) TryDeleteFiles();
        }
    }

    public sealed class PreparedUpdateInstall : IDisposable
    {
        private Process helper;
        private StagedUpdate staged;
        private bool detached;

        internal PreparedUpdateInstall(Process helper, StagedUpdate staged)
        {
            this.helper = helper;
            this.staged = staged;
        }

        // Call only once patch restoration succeeded and the application is about to exit.
        public void Detach()
        {
            if (helper == null) throw new ObjectDisposedException(nameof(PreparedUpdateInstall));
            detached = true;
            helper.Dispose();
            helper = null;
            staged = null;
        }

        public void Dispose()
        {
            if (helper == null || detached) return;
            bool exited = UpdateInstaller.StopOwnedHelper(helper);
            helper.Dispose();
            helper = null;
            if (exited) staged.TryDeleteFiles();
            staged = null;
        }
    }

    public sealed class UpdateInstaller
    {
        private const long MaximumPackageBytes = 512L * 1024 * 1024;
        private const string StagePrefix = "HSRUnlocker-Update-";
        private readonly HttpClient client;
        private readonly string fixtureRoot;
        private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier SystemIdentity = new(WellKnownSidType.LocalSystemSid, null);

        public UpdateInstaller(HttpClient client) => this.client = client ?? throw new ArgumentNullException(nameof(client));

        // Fixture staging is deliberately ineligible for an installer handoff.
        internal UpdateInstaller(HttpClient client, string fixtureRoot) : this(client) =>
            this.fixtureRoot = Path.GetFullPath(fixtureRoot ?? throw new ArgumentNullException(nameof(fixtureRoot)));

        public async Task<StagedUpdate> DownloadAsync(UpdatePackage package, IProgress<double> progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePackage(package);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            CancellationToken token = timeout.Token;
            StagedUpdate staged = CreateStaging(package);
            try
            {
                progress?.Report(0);
                Uri[] locations = UpdateSources.DownloadLocations(package);
                int lastPercent = 0;
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        await DownloadAttemptAsync(staged, locations[attempt], percent =>
                        {
                            if (percent <= lastPercent) return;
                            lastPercent = percent;
                            progress?.Report(percent / 100.0);
                        }, token).ConfigureAwait(false);
                        break;
                    }
                    catch (RemoteDownloadException error)
                    {
                        token.ThrowIfCancellationRequested();
                        if (attempt + 1 >= locations.Length)
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.Failure).Throw();
                        RejectReparseAncestors(staged.DirectoryPath);
                        File.Delete(Path.Combine(staged.DirectoryPath, "download.bin"));
                    }
                }
                token.ThrowIfCancellationRequested();
                File.Move(Path.Combine(staged.DirectoryPath, "download.bin"), staged.FilePath);
                progress?.Report(1);
                return staged;
            }
            catch
            {
                staged.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        private async Task DownloadAttemptAsync(StagedUpdate staged, Uri uri, Action<int> report, CancellationToken token)
        {
            UpdatePackage package = staged.Package;
            bool gitee = uri.Host == "gitee.com";
            string tag = uri.Segments[^2].TrimEnd('/');
            using HttpResponseMessage response = await ReadRemoteAsync(
                readToken => UpdateSources.GetFileAsync(client, uri, package.FileName, gitee, readToken, tag), token).ConfigureAwait(false);
            long? declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength.HasValue && declaredLength.Value != package.Size)
                throw new InvalidDataException("The installer response length does not match the release asset.");

            using Stream source = await ReadRemoteAsync(
                readToken => response.Content.ReadAsStreamAsync(readToken), token).ConfigureAwait(false);
            using FileStream destination = CreateDownloadFile(staged);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65536];
            long total = 0;
            while (true)
            {
                int count = await ReadRemoteAsync(
                    readToken => source.ReadAsync(buffer.AsMemory(), readToken).AsTask(), token).ConfigureAwait(false);
                if (count == 0) break;
                if (count > package.Size - total)
                    throw new InvalidDataException("The installer download exceeds the release asset size.");
                await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);
                total += count;
                report(Math.Min(99, (int)(total * 100 / package.Size)));
            }
            token.ThrowIfCancellationRequested();
            if (total != package.Size
                || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(package.Sha256)))
                throw new InvalidDataException("The installer download failed its size or SHA256 verification.");
            await destination.FlushAsync(token).ConfigureAwait(false);
            destination.Flush(true);
        }

        // Only remote operations enter this boundary. Local staging failures never trigger provider retry.
        private static async Task<T> ReadRemoteAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken token)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(TimeSpan.FromSeconds(8));
            try { return await read(idle.Token).ConfigureAwait(false); }
            catch (Exception error) when (UpdateSources.IsProtocolLengthFailure(error))
            {
                throw new InvalidDataException("The installer response ended before its declared length.");
            }
            catch (Exception error) when (UpdateSources.IsTransportFailure(error) && !token.IsCancellationRequested)
            {
                throw new RemoteDownloadException(UpdateSources.SafeFailure(error));
            }
        }

        private sealed class RemoteDownloadException : Exception
        {
            internal Exception Failure { get; }
            internal RemoteDownloadException(Exception failure) : base("The remote installer request failed.") => Failure = failure;
        }

        public async Task<PreparedUpdateInstall> PrepareInstallAsync(StagedUpdate staged, string localizedError,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(staged);
            cancellationToken.ThrowIfCancellationRequested();
            staged.EnsureAvailable();
            if (!staged.IsProtected)
                throw new InvalidOperationException("Fixture staging cannot be executed.");
            ValidatePackage(staged.Package);
            ValidateProtectedStaging(staged.DirectoryPath, staged.FilePath);
            if (string.IsNullOrWhiteSpace(localizedError) || localizedError.Length > 4096)
                throw new ArgumentException("An update error message is required.", nameof(localizedError));

            using Process parent = Process.GetCurrentProcess();
            ProcessStartInfo start = UpdateInstallHelper.StartInfo(staged, parent.Id,
                parent.StartTime.ToUniversalTime().Ticks, localizedError);
            Process helper = null;
            try
            {
                helper = Process.Start(start) ?? throw new InvalidOperationException("The installer handoff helper did not start.");
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshake.CancelAfter(TimeSpan.FromSeconds(30));
                string ready = await helper.StandardOutput.ReadLineAsync(handshake.Token).ConfigureAwait(false);
                if (ready != "READY" || helper.HasExited)
                    throw new InvalidOperationException("The installer handoff helper could not verify the staged installer.");
                cancellationToken.ThrowIfCancellationRequested();
                staged.HandOff();
                return new PreparedUpdateInstall(helper, staged);
            }
            catch
            {
                if (helper != null)
                {
                    StopOwnedHelper(helper);
                    helper.Dispose();
                }
                throw;
            }
        }

        internal static bool StopOwnedHelper(Process helper)
        {
            try
            {
                if (!helper.HasExited) helper.Kill();
                return helper.WaitForExit(5000);
            }
            catch (InvalidOperationException) { return true; }
            catch (Win32Exception) { return false; }
        }

        private static void ValidatePackage(UpdatePackage package)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            if (package.Version == null || package.Version.Length > 64
                || !Regex.IsMatch(package.Version, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*))?\z",
                    RegexOptions.CultureInvariant)
                || !Version.TryParse(package.Version, out _)
                || package.FileName != "HSRUnlocker-" + package.Version + "-win-x64-Setup.exe"
                || package.Size < 1 || package.Size > MaximumPackageBytes
                || package.Sha256 == null || !Regex.IsMatch(package.Sha256, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)
                || package.DownloadUri == null || !package.DownloadUri.IsAbsoluteUri)
                throw new ArgumentException("The installer package metadata is invalid.", nameof(package));
            _ = UpdateSources.DownloadLocations(package);
        }

        private StagedUpdate CreateStaging(UpdatePackage package)
        {
            string root = fixtureRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("The system update staging directory is unavailable.");
            RejectReparseAncestors(root);
            string directory = Path.Combine(root, StagePrefix + Guid.NewGuid().ToString("N"));
            if (fixtureRoot != null)
            {
                if (Directory.Exists(directory)) throw new IOException("The update staging directory already exists.");
                Directory.CreateDirectory(directory);
                return new StagedUpdate(package, directory, false, null);
            }
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var security = new DirectorySecurity();
            security.SetOwner(Administrators);
            security.SetAccessRuleProtection(true, false);
            foreach (SecurityIdentifier identity in new[] { Administrators, SystemIdentity })
                security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            WithSecurity(security.GetSecurityDescriptorBinaryForm(), attributes =>
            {
                if (!CreateDirectoryNative(directory, ref attributes))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the protected update directory.");
                return true;
            });
            try
            {
                ValidateProtectedStaging(directory, null);
                SafeFileHandle handle = OpenDirectory(directory, 0, FileShare.Read | FileShare.Write,
                    IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not lock the update staging directory.");
                }
                return new StagedUpdate(package, directory, true, handle);
            }
            catch
            {
                Directory.Delete(directory);
                throw;
            }
        }

        private static FileStream CreateDownloadFile(StagedUpdate staged)
        {
            string path = Path.Combine(staged.DirectoryPath, "download.bin");
            if (!staged.IsProtected)
                return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            var security = new FileSecurity();
            security.SetOwner(Administrators);
            security.SetAccessRuleProtection(true, false);
            foreach (SecurityIdentifier identity in new[] { Administrators, SystemIdentity })
                security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
            SafeFileHandle handle = WithSecurity(security.GetSecurityDescriptorBinaryForm(), attributes =>
                CreateSecureFile(path, 0x40000000, 0, ref attributes, 1, 0x48000000, IntPtr.Zero));
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the protected update download.");
            }
            try { return new FileStream(handle, FileAccess.Write, 65536, true); }
            catch { handle.Dispose(); throw; }
        }

        internal static void RejectReparseAncestors(string path)
        {
            DirectoryInfo directory = new(Path.GetFullPath(path));
            for (; directory != null; directory = directory.Parent)
            {
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Update staging cannot use a missing directory or reparse point.");
            }
        }

        private static void ValidateProtectedStaging(string directoryPath, string installerPath)
        {
            string root = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            var directory = new DirectoryInfo(directoryPath);
            if (directory.Parent == null || !directory.Parent.FullName.Equals(root, StringComparison.OrdinalIgnoreCase)
                || !Regex.IsMatch(directory.Name, @"\AHSRUnlocker-Update-[0-9a-f]{32}\z", RegexOptions.CultureInvariant))
                throw new IOException("The protected update directory is outside the system staging location.");
            RejectReparseAncestors(directoryPath);
            ValidateSecurity(directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
            if (installerPath == null) return;
            var file = new FileInfo(installerPath);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0
                || !file.Directory.FullName.Equals(directory.FullName, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The staged installer path is invalid.");
            ValidateSecurity(file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
        }

        private static void ValidateSecurity(FileSystemSecurity security)
        {
            if (!security.AreAccessRulesProtected || !security.GetOwner(typeof(SecurityIdentifier)).Equals(Administrators))
                throw new IOException("Update staging has an unsafe security descriptor.");
            AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            bool admin = false;
            bool system = false;
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.IsInherited || rule.AccessControlType != AccessControlType.Allow
                    || rule.FileSystemRights != FileSystemRights.FullControl)
                    throw new IOException("Update staging has an unsafe access rule.");
                if (rule.IdentityReference.Equals(Administrators)) admin = true;
                else if (rule.IdentityReference.Equals(SystemIdentity)) system = true;
                else throw new IOException("Update staging grants access outside Administrators and SYSTEM.");
            }
            if (!admin || !system || rules.Count != 2)
                throw new IOException("Update staging is missing its required access rules.");
        }

        private static T WithSecurity<T>(byte[] descriptor, Func<SecurityAttributes, T> action)
        {
            IntPtr memory = Marshal.AllocHGlobal(descriptor.Length);
            try
            {
                Marshal.Copy(descriptor, 0, memory, descriptor.Length);
                return action(new SecurityAttributes
                {
                    Length = Marshal.SizeOf<SecurityAttributes>(),
                    SecurityDescriptor = memory,
                    InheritHandle = 0
                });
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            public int InheritHandle;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryNative(string path, ref SecurityAttributes security);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateSecureFile(string path, uint access, uint share, ref SecurityAttributes security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle OpenDirectory(string path, uint access, FileShare share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
    }
}
