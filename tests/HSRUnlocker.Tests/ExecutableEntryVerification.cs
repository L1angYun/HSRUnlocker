using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using StarRailUnlocker.Core;

internal static class ExecutableEntryVerification
{
    private const string ChildArgument = "--entry-fixture";
    private const string DirectoryPrefix = "HSRUnlocker-entry-";
    private const int PageSize = 0x1000;
    // Leave time for native-thread cleanup before the child's 30-second lifetime expires.
    private const int ChildWorkMilliseconds = 28_000;
    private static readonly byte[] Prologue = { 0x41, 0x56, 0x56, 0x57, 0x55, 0x53, 0x48, 0x83, 0xEC, 0x50 };

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate float EntryCall(IntPtr state, float alpha, int kind, ulong flags);

    /// <summary>Call before help handling or argument rejection in Program.Main.</summary>
    public static bool TryRunChild(string[] args, out int result)
    {
        result = 0;
        if (args.Length == 0 || args[0] != ChildArgument) return false;
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Entry fixture requires exactly one own GUID temporary directory.");
            result = 2;
            return true;
        }
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("The native entry fixture requires Windows x64.");
            result = RunChild(ValidateDirectory(args[1]));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Own entry-fixture child failed: " + ex);
            result = 1;
        }
        return true;
    }

    public static void Run(Action<bool, string> check)
    {
        VerifyPureRefusals(check);
        string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), DirectoryPrefix + Guid.NewGuid().ToString("N")));
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new InvalidOperationException("Refusing to reuse an existing entry-fixture directory.");
        Directory.CreateDirectory(directory);
        ChildSession child = null;
        try
        {
            child = new ChildSession(directory);
            Ready ready = child.WaitReady();
            ValidateReady(ready, child.Pid, directory);
            using var process = new TargetProcess(checked((uint)child.Pid));
            check(ReadCode(process, ready, ready.Entry, 10).AsSpan().SequenceEqual(Prologue)
                && HasProtection(process, ready, ready.CodeBase, Native.PAGE_EXECUTE_READ)
                && HasProtection(process, ready, ready.State, Native.PAGE_READWRITE),
                "own bounded child exposes the exact ten-byte entry in RX private code, separate from RW state");
            check(ready.WorkerThreadId != 0 && ready.Heartbeat != 0,
                "own child has a live harmless native heartbeat thread for real suspension/context checks");

            ulong calls = 0;
            void VerifyCall(int alphaBits, int kind, ulong flags, int expectedBits, string description)
            {
                Reply reply = child.Request("call", alphaBits, kind, flags);
                calls++;
                check(reply.ResultBits == expectedBits && reply.AlphaBits == expectedBits
                    && reply.Kind == kind && reply.Flags == flags && reply.ReceivedState == ready.State
                    && reply.Counter == calls && reply.Pid == child.Pid, description);
            }
            int quarter = BitConverter.SingleToInt32Bits(0.25f), one = BitConverter.SingleToInt32Bits(1f);
            VerifyCall(quarter, 1, 0x1122334455667788UL, quarter,
                "before installation the real child entry receives/returns alpha 0.25, kind 1 and full RCX/R9 arguments");

            var patch = new ExecutableEntryPatch(process, ready.Entry, Prologue);
            Reply parked = child.Request("park-guard");
            check(parked.GuardParked && parked.InstructionPointer == ready.Entry
                && parked.WorkerThreadId == ready.WorkerThreadId,
                "child parks only its own native worker at an entry RIP while retaining one explicit suspension");
            check(!patch.Install(ready.Wrapper) && !patch.IsInstalled && !patch.WasPublished
                && patch.LastError?.Contains("entering", StringComparison.OrdinalIgnoreCase) == true
                && ReadCode(process, ready, ready.Entry, 10).AsSpan().SequenceEqual(Prologue)
                && HasProtection(process, ready, ready.Entry, Native.PAGE_EXECUTE_READ),
                "actual child thread guard refuses an in-flight entry without publishing bytes or changing RX protection");
            Reply stillParked = child.Request("status");
            check(stillParked.GuardParked && stillParked.Heartbeat == parked.Heartbeat,
                "failed thread guard does not resume the child's independently owned suspension");
            Reply released = child.Request("release-guard");
            check(!released.GuardParked && released.ResumeCount == 1
                && released.InstructionPointer == ready.Entry && released.Heartbeat != parked.Heartbeat,
                "thread guard balances exactly its own suspend increment; restored native worker runs again");

            byte[] originalWord = Prologue.AsSpan(0, 8).ToArray();
            byte[] installedWord = ExecutableEntryPatch.BuildEntryWord(ready.Entry, ready.Wrapper);
            byte[] foreignWord = ExecutableEntryPatch.BuildEntryWord(ready.Entry, ready.ForeignStub);
            WriteOwnEntryWord(process, ready, originalWord, foreignWord);
            check(!patch.Install(ready.Wrapper) && !patch.IsInstalled && !patch.WasPublished
                && IsOwnershipError(patch) && ReadCode(process, ready, ready.Entry, 8).AsSpan().SequenceEqual(foreignWord)
                && HasProtection(process, ready, ready.Entry, Native.PAGE_EXECUTE_READ),
                "Install refuses a competing own allocated-stub word without overwriting it or changing RX protection");
            WriteOwnEntryWord(process, ready, foreignWord, originalWord);

            check(patch.Install(ready.Wrapper) && patch.IsInstalled && patch.WasPublished
                && ReadCode(process, ready, ready.Entry, 8).AsSpan().SequenceEqual(installedWord)
                && ReadCode(process, ready, ready.Entry + 8, 2).AsSpan().SequenceEqual(Prologue.AsSpan(8))
                && HasProtection(process, ready, ready.Entry, Native.PAGE_EXECUTE_READ),
                "real Install publishes just the aligned entry word, retains the two-byte suffix and restores the RX page");
            Reply pulse = child.Request("pulse");
            check(pulse.Heartbeat != released.Heartbeat,
                "all real child threads resume and native heartbeat progresses after successful Install");
            VerifyCall(quarter, 1, 0xFEDCBA9876543210UL, one,
                "installed native entry/gateway changes kind-1 alpha 0.25 to 1 while preserving RCX, R8, R9 and exact call count");
            VerifyCall(quarter, 2, 0x8123456789ABCDEFUL, quarter,
                "installed wrapper leaves kind-2 alpha 0.25 unchanged and preserves other register arguments/counter");
            VerifyCall(unchecked((int)0x80000000), 0, ulong.MaxValue, unchecked((int)0x80000000),
                "only kind 1 is changed: kind-0 negative-zero bits and all flag bits survive the native gateway");
            VerifyCall(0x7FC01234, -1, 0x0102030405060708UL, 0x7FC01234,
                "non-kind-1 NaN payload and signed kind survive without float arithmetic or argument truncation");
            check(patch.Install(ready.Wrapper)
                && ReadCode(process, ready, ready.Entry, 8).AsSpan().SequenceEqual(installedWord)
                && HasProtection(process, ready, ready.Entry, Native.PAGE_EXECUTE_READ),
                "reinstalling the same owned wrapper is idempotent and leaves code RX");
            check(!patch.Install(ready.ForeignStub)
                && ReadCode(process, ready, ready.Entry, 8).AsSpan().SequenceEqual(installedWord)
                && HasProtection(process, ready, ready.Entry, Native.PAGE_EXECUTE_READ),
                "an installed publication cannot be silently redirected to a different wrapper");

            WriteOwnEntryWord(process, ready, installedWord, foreignWord);
            check(!patch.Remove() && IsOwnershipError(patch)
                && ReadCode(process, ready, ready.Entry, 8).AsSpan().SequenceEqual(foreignWord)
                && HasProtection(process, ready, ready.Entry, Native.PAGE_EXECUTE_READ),
                "Remove refuses foreign entry ownership and preserves the competing word and RX page");
            check(!patch.Install(ready.Wrapper) && IsOwnershipError(patch)
                && ReadCode(process, ready, ready.Entry, 8).AsSpan().SequenceEqual(foreignWord)
                && HasProtection(process, ready, ready.Entry, Native.PAGE_EXECUTE_READ),
                "installed-state Install checks actual ownership instead of reporting stale success over foreign code");
            VerifyCall(quarter, 1, 0x8877665544332211UL, quarter,
                "the competing word points only to the child's harmless allocated pass-through stub");
            // Restore our known published word, not someone else's original bytes, before asking Remove to act.
            WriteOwnEntryWord(process, ready, foreignWord, installedWord);
            check(patch.Remove() && !patch.IsInstalled && patch.WasPublished
                && ReadCode(process, ready, ready.Entry, 10).AsSpan().SequenceEqual(Prologue)
                && HasProtection(process, ready, ready.Entry, Native.PAGE_EXECUTE_READ)
                && HasProtection(process, ready, ready.Gateway, Native.PAGE_EXECUTE_READ)
                && HasProtection(process, ready, ready.State, Native.PAGE_READWRITE),
                "Remove restores the exact original ten bytes and RX permissions while state remains RW");
            VerifyCall(quarter, 1, 0xA5A55A5AF0F00F0FUL, quarter,
                "after Remove the original entry again receives/returns kind-1 alpha 0.25 with arguments/counter intact");
            check(patch.Remove() && ReadCode(process, ready, ready.Entry, 10).AsSpan().SequenceEqual(Prologue)
                && HasProtection(process, ready, ready.Entry, Native.PAGE_EXECUTE_READ)
                && child.Request("pulse").Heartbeat != pulse.Heartbeat,
                "repeated Remove is harmless and all child threads remain runnable after restoration");

            check(patch.Install(ready.Wrapper), "prepare an owned child entry whose publication acknowledgement will be lost");
            typeof(ExecutableEntryPatch).GetProperty(nameof(ExecutableEntryPatch.IsInstalled))
                .SetValue(patch, false);
            check(patch.Remove() && !patch.IsInstalled
                && ReadCode(process, ready, ready.Entry, 10).AsSpan().SequenceEqual(Prologue)
                && HasProtection(process, ready, ready.Entry, Native.PAGE_EXECUTE_READ),
                "Remove reconciles a published owned jump even when its installation acknowledgement was lost");
        }
        finally
        {
            // Never kill: the quit request and the child's independent bounded lifetime are the only exit mechanisms.
            if (child != null) child.Dispose();
            else DeleteOwnDirectory(directory);
        }
        check(child.ExitedCleanly && !Directory.Exists(directory),
            "own fixture child acknowledges graceful quit, frees settled native allocations and removes its GUID temporary files");
    }

    private static void VerifyPureRefusals(Action<bool, string> check)
    {
        check(ExecutableEntryPatch.BuildEntryWord(0x1000, 0x1200).AsSpan().SequenceEqual(
                new byte[] { 0xE9, 0xFB, 0x01, 0, 0, 0x0F, 0x1F, 0 }),
            "pure entry-word builder emits a five-byte rel32 jump plus the exact three-byte NOP");
        const ulong entry = 0x100000000;
        check(BitConverter.ToInt32(ExecutableEntryPatch.BuildEntryWord(entry, entry + 5 + int.MaxValue), 1) == int.MaxValue
            && BitConverter.ToInt32(ExecutableEntryPatch.BuildEntryWord(entry, entry + 5 - 0x80000000UL), 1) == int.MinValue,
            "pure rel32 builder accepts both exact signed displacement limits");
        foreach (ulong wrapper in new[] { entry + 5 + 0x80000000UL, entry + 5 - 0x80000001UL })
        {
            bool refused = false;
            try { ExecutableEntryPatch.BuildEntryWord(entry, wrapper); }
            catch (OverflowException) { refused = true; }
            check(refused, "pure rel32 builder refuses an out-of-range wrapper before any process operation");
        }
        using var own = new TargetProcess(checked((uint)Environment.ProcessId), readOnly: true);
        foreach (byte[] bad in new[] { Array.Empty<byte>(), Prologue.AsSpan(0, 9).ToArray(),
            new byte[] { 0x41, 0x56, 0x56, 0x57, 0x55, 0x53, 0x48, 0x83, 0xEC, 0x58 }, null })
        {
            bool refused = false;
            try { _ = new ExecutableEntryPatch(own, 0x1000, bad); }
            catch (NotSupportedException) { refused = true; }
            check(refused, "constructor purely refuses an unsupported/null/truncated native prologue without reading code");
        }
        foreach (ulong address in new ulong[] { 0, 0x1001 })
        {
            bool refused = false;
            try { _ = new ExecutableEntryPatch(own, address, Prologue); }
            catch (ArgumentException) { refused = true; }
            check(refused, "constructor refuses zero/unaligned entries before touching target memory");
        }
        var self = new ExecutableEntryPatch(own, 0x1000, Prologue);
        check(!self.Install(0x1200) && !self.IsInstalled && !self.WasPublished
            && self.LastError?.Contains("own process", StringComparison.OrdinalIgnoreCase) == true && self.Remove(),
            "own-process Install refuses before suspension or memory access even with a valid constructor/prologue");
    }

    private static bool IsOwnershipError(ExecutableEntryPatch patch) =>
        patch.LastError?.Contains("ownership", StringComparison.OrdinalIgnoreCase) == true;

    private sealed class Ready
    {
        public int Pid { get; set; }
        public string Token { get; set; }
        public string Assembly { get; set; }
        public ulong State { get; set; }
        public ulong CodeBase { get; set; }
        public int CodeSize { get; set; }
        public ulong Entry { get; set; }
        public ulong Wrapper { get; set; }
        public ulong Gateway { get; set; }
        public ulong ForeignStub { get; set; }
        public uint WorkerThreadId { get; set; }
        public ulong Heartbeat { get; set; }
    }

    private sealed class Request
    {
        public int Id { get; set; }
        public string Command { get; set; }
        public int AlphaBits { get; set; }
        public int Kind { get; set; }
        public ulong Flags { get; set; }
    }

    private sealed class Reply
    {
        public int Id { get; set; }
        public int Pid { get; set; }
        public string Command { get; set; }
        public string Error { get; set; }
        public int ResultBits { get; set; }
        public int AlphaBits { get; set; }
        public int Kind { get; set; }
        public ulong Flags { get; set; }
        public ulong ReceivedState { get; set; }
        public ulong Counter { get; set; }
        public ulong Heartbeat { get; set; }
        public bool GuardParked { get; set; }
        public uint WorkerThreadId { get; set; }
        public uint ResumeCount { get; set; }
        public ulong InstructionPointer { get; set; }
    }

    private static string ValidateDirectory(string directory)
    {
        string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase)
            || !leaf.StartsWith(DirectoryPrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(leaf[DirectoryPrefix.Length..], "N", out _)
            || !Directory.Exists(full)
            || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Entry fixture accepts only its direct, non-reparse GUID directory under the OS temp directory.");
        return full;
    }

    private static void DeleteOwnDirectory(string directory)
    {
        // Resolve and verify the exact parent-created target before recursive deletion.
        string verified = ValidateDirectory(directory);
        Directory.Delete(verified, recursive: true);
    }

    private static void WriteJson<T>(string path, T value)
    {
        // Every protocol file is immutable: no existing temp file is ever overwritten.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value);
    }

    private static T ReadJson<T>(string path) where T : class
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16 * 1024) throw new InvalidOperationException("Oversized own-fixture protocol file.");
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidOperationException("Empty own-fixture protocol value.");
    }

    private static void ValidateReady(Ready ready, int pid, string directory)
    {
        if (ready.Pid != pid || pid == Environment.ProcessId
            || ready.Token != Path.GetFileName(directory)[DirectoryPrefix.Length..]
            || !string.Equals(Path.GetFullPath(ready.Assembly), Path.GetFullPath(typeof(ExecutableEntryVerification).Assembly.Location), StringComparison.OrdinalIgnoreCase)
            || ready.CodeSize != PageSize || ready.CodeBase == 0 || ready.State == 0
            || ready.CodeBase > (ulong)long.MaxValue - PageSize || ready.State > (ulong)long.MaxValue - PageSize
            || ready.CodeBase % PageSize != 0 || ready.State % PageSize != 0
            || ready.CodeBase == ready.State || ready.Entry != ready.CodeBase
            || ready.Wrapper != ready.CodeBase + 0x100 || ready.Gateway != ready.CodeBase + 0x200
            || ready.ForeignStub != ready.CodeBase + 0x300)
            throw new InvalidOperationException("Ready data does not describe this exact child and its bounded private allocations.");
    }

    private sealed class ChildSession : IDisposable
    {
        private readonly string _directory;
        private readonly Process _process;
        private readonly Stopwatch _lifetime = Stopwatch.StartNew();
        private int _sequence;
        public int Pid { get; }
        public bool ExitedCleanly { get; private set; }

        public ChildSession(string directory)
        {
            _directory = ValidateDirectory(directory);
            string assembly = typeof(ExecutableEntryVerification).Assembly.Location;
            string host = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot identify the current test host.");
            if (string.IsNullOrEmpty(assembly) || Assembly.GetEntryAssembly()?.Location != assembly)
                throw new InvalidOperationException("Fixture can only relaunch the current test entry assembly.");
            bool dotnet = string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase);
            if (!dotnet && !string.Equals(Path.GetFullPath(host), Path.ChangeExtension(Path.GetFullPath(assembly), ".exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture host is not the current assembly's own apphost.");
            var start = new ProcessStartInfo
            {
                FileName = host,
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                WorkingDirectory = AppContext.BaseDirectory
            };
            if (dotnet) start.ArgumentList.Add(assembly);
            start.ArgumentList.Add(ChildArgument);
            start.ArgumentList.Add(_directory);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the own test-assembly child.");
            Pid = _process.Id;
        }

        public Ready WaitReady() => WaitJson<Ready>("ready.json");

        public Reply Request(string command, int alphaBits = 0, int kind = 0, ulong flags = 0)
        {
            int id = ++_sequence;
            WriteJson(Path.Combine(_directory, $"request-{id:D3}.json"), new Request
            {
                Id = id, Command = command, AlphaBits = alphaBits, Kind = kind, Flags = flags
            });
            Reply reply = WaitJson<Reply>($"reply-{id:D3}.json");
            if (reply.Id != id || reply.Pid != Pid || reply.Command != command || reply.Error != null)
                throw new InvalidOperationException("Own entry child rejected request: " + (reply.Error ?? "response identity mismatch"));
            return reply;
        }

        private T WaitJson<T>(string name) where T : class
        {
            string path = Path.Combine(_directory, name);
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 5000)
            {
                if (File.Exists(path))
                {
                    try { return ReadJson<T>(path); }
                    catch (IOException) { /* The creator keeps the file exclusively open until serialization ends. */ }
                }
                if (_process.HasExited)
                    throw new InvalidOperationException($"Own entry child exited before {name} (exit {_process.ExitCode}).");
                Thread.Sleep(5);
            }
            throw new TimeoutException("Own entry child did not publish " + name + " within five seconds.");
        }

        public void Dispose()
        {
            try
            {
                try
                {
                    string quit = Path.Combine(_directory, "quit.json");
                    if (!File.Exists(quit)) WriteJson(quit, new { quit = true });
                }
                finally
                {
                    int remaining = Math.Max(1, 31_000 - checked((int)Math.Min(_lifetime.ElapsedMilliseconds, 31_000)));
                    if (!_process.HasExited && !_process.WaitForExit(remaining))
                        throw new TimeoutException("Own entry child exceeded its bounded lifetime; no force-kill was attempted.");
                }
                ExitedCleanly = _process.ExitCode == 0;
                if (!ExitedCleanly) throw new InvalidOperationException("Own entry child failed with exit " + _process.ExitCode);
            }
            finally
            {
                // If an unexpected live child remains, retain its private directory for its bounded cleanup.
                if (_process.HasExited) DeleteOwnDirectory(_directory);
                _process.Dispose();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInformation
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MemoryInformation information, UIntPtr size);

    private static void CheckCodeRange(TargetProcess process, Ready ready, ulong address, int length)
    {
        if (process.Pid != (uint)ready.Pid || process.Pid == (uint)Environment.ProcessId
            || length < 0 || length > ready.CodeSize || address < ready.CodeBase
            || address - ready.CodeBase > (ulong)(ready.CodeSize - length))
            throw new InvalidOperationException("Refusing memory access outside this child's own code allocation.");
    }

    private static byte[] ReadCode(TargetProcess process, Ready ready, ulong address, int length)
    {
        CheckCodeRange(process, ready, address, length);
        return process.Read(address, length) ?? throw new InvalidOperationException("Cannot read bounded own-child code.");
    }

    private static bool HasProtection(TargetProcess process, Ready ready, ulong address, uint expected)
    {
        bool state = address == ready.State;
        if (!state) CheckCodeRange(process, ready, address, 1);
        if (process.Pid != (uint)ready.Pid || process.Pid == (uint)Environment.ProcessId)
            throw new InvalidOperationException("Refusing to query a process other than the owned fixture child.");
        if (VirtualQueryEx(process.Handle, (IntPtr)(long)address, out var information,
                (UIntPtr)Marshal.SizeOf<MemoryInformation>()) == UIntPtr.Zero)
            throw new InvalidOperationException("Cannot inspect bounded child page protection: " + Marshal.GetLastWin32Error());
        ulong allocation = state ? ready.State : ready.CodeBase;
        return information.AllocationBase == (IntPtr)(long)allocation && information.State == Native.MEM_COMMIT
            && information.Type == 0x20000 /* MEM_PRIVATE */ && information.Protect == expected;
    }

    private static void WriteOwnEntryWord(TargetProcess process, Ready ready, byte[] expected, byte[] replacement)
    {
        // The parent may alter only this aligned word in the child's explicitly authored code, never RW state.
        CheckCodeRange(process, ready, ready.Entry, 8);
        if (expected.Length != 8 || replacement.Length != 8
            || !ReadCode(process, ready, ready.Entry, 8).AsSpan().SequenceEqual(expected)
            || !HasProtection(process, ready, ready.Entry, Native.PAGE_EXECUTE_READ))
            throw new InvalidOperationException("Unexpected own entry word/protection before a reversible fixture-only edit.");
        if (!process.WriteBytes(ready.Entry, replacement)
            || !ReadCode(process, ready, ready.Entry, 8).AsSpan().SequenceEqual(replacement)
            || !HasProtection(process, ready, ready.Entry, Native.PAGE_EXECUTE_READ))
            throw new InvalidOperationException("Cannot reversibly edit the own allocated entry word and restore RX.");
    }

    private static int RunChild(string directory)
    {
        var lifetime = Stopwatch.StartNew();
        using var fixture = new NativeFixture();
        WriteJson(Path.Combine(directory, "ready.json"), fixture.Ready(directory));
        int next = 1;
        while (lifetime.ElapsedMilliseconds < ChildWorkMilliseconds && next <= 64)
        {
            if (File.Exists(Path.Combine(directory, "quit.json"))) return 0;
            string path = Path.Combine(directory, $"request-{next:D3}.json");
            if (!File.Exists(path)) { Thread.Sleep(5); continue; }
            Request request;
            try { request = ReadJson<Request>(path); }
            catch (IOException) { Thread.Sleep(2); continue; }
            Reply reply;
            try
            {
                if (request.Id != next) throw new InvalidOperationException("Out-of-order own-fixture request.");
                reply = fixture.Handle(request);
            }
            catch (Exception ex) { reply = new Reply { Error = ex.Message }; }
            reply.Id = next;
            reply.Pid = Environment.ProcessId;
            reply.Command = request.Command;
            WriteJson(Path.Combine(directory, $"reply-{next:D3}.json"), reply);
            next++;
        }
        return 0;
    }

    private static byte[] Jump(ulong source, ulong target)
    {
        var result = new byte[] { 0xE9, 0, 0, 0, 0 };
        BitConverter.GetBytes(checked((int)(checked((long)target) - checked((long)source + 5)))).CopyTo(result, 1);
        return result;
    }

    private sealed class NativeFixture : IDisposable
    {
        private readonly TargetProcess _self;
        private readonly EntryCall _call;
        private ulong _state, _code;
        private IntPtr _worker;
        private uint _workerId;
        private IntPtr _contextStorage, _savedContext;
        private bool _parked, _disposed;
        private const int StopOffset = 0x80, HeartbeatOffset = 0x88;
        private const int ContextSize = 1232, ContextFlagsOffset = 48, RipOffset = 248;
        private const int ControlContext = 0x00100001;
        private ulong Entry => _code;
        private ulong Wrapper => _code + 0x100;
        private ulong Gateway => _code + 0x200;
        private ulong ForeignStub => _code + 0x300;
        private ulong WorkerCode => _code + 0x400;
        private ulong Heartbeat => unchecked((ulong)Marshal.ReadInt64((IntPtr)(long)_state, HeartbeatOffset));

        public NativeFixture()
        {
            _self = new TargetProcess(checked((uint)Environment.ProcessId));
            try
            {
                _state = _self.Allocate(PageSize);
                _code = _self.Allocate(PageSize);
                if (_state == 0 || _code == 0) throw new InvalidOperationException("Own child fixture allocation failed.");
                // Win64 RCX = own RW state, XMM1 = alpha, R8D = kind, R9 = flags.
                // Record arguments/call count, return alpha, and unwind all five nonvolatile pushes.
                byte[] body = { 0xF3,0x0F,0x11,0x09, 0x44,0x89,0x41,0x04, 0x4C,0x89,0x49,0x08,
                    0x48,0x89,0x49,0x10, 0xF0,0x48,0xFF,0x41,0x18, 0xF3,0x0F,0x10,0xC1,
                    0x48,0x83,0xC4,0x50, 0x5B,0x5D,0x5F,0x5E,0x41,0x5E,0xC3 };
                byte[] entry = Prologue.Concat(body).ToArray();
                byte[] gateway = Prologue.Concat(Jump(Gateway + 10, Entry + 10)).ToArray();
                // CMP R8D,1; JNE gateway jump; MOV EAX,1.0f; MOVD XMM1,EAX; JMP gateway.
                byte[] wrapperHead = { 0x41,0x83,0xF8,0x01, 0x75,0x09, 0xB8,0,0,0x80,0x3F, 0x66,0x0F,0x6E,0xC8 };
                byte[] wrapper = wrapperHead.Concat(Jump(Wrapper + (ulong)wrapperHead.Length, Gateway)).ToArray();
                // A native-only, leaf heartbeat loop: PAUSE; test own stop flag; INC own heartbeat; repeat; return 0.
                // No CLR locks are involved when this worker is suspended by the fixture itself.
                byte[] worker = { 0xF3,0x90, 0x83,0x39,0x00, 0x75,0x06, 0x48,0xFF,0x41,0x08, 0xEB,0xF3, 0x31,0xC0,0xC3 };
                if (!_self.WriteData(Entry, entry) || !_self.WriteData(Wrapper, wrapper)
                    || !_self.WriteData(Gateway, gateway) || !_self.WriteData(ForeignStub, Jump(ForeignStub, Gateway))
                    || !_self.WriteData(WorkerCode, worker) || !_self.MakeExecutable(_code, PageSize))
                    throw new InvalidOperationException("Cannot prepare own RX entry/gateway/wrapper/worker code.");
                _call = Marshal.GetDelegateForFunctionPointer<EntryCall>((IntPtr)(long)Entry);
                _worker = Native.CreateRemoteThread(_self.Handle, IntPtr.Zero, UIntPtr.Zero,
                    (IntPtr)(long)WorkerCode, (IntPtr)(long)(_state + StopOffset), 0, out _workerId);
                if (_worker == IntPtr.Zero) throw new InvalidOperationException("Cannot create own native fixture worker.");
                _savedContext = AllocateContext(out _contextStorage);
                WaitForPulse();
            }
            catch { Dispose(); throw; }
        }

        public Ready Ready(string directory) => new Ready
        {
            Pid = Environment.ProcessId, Token = Path.GetFileName(directory)[DirectoryPrefix.Length..],
            Assembly = typeof(ExecutableEntryVerification).Assembly.Location, State = _state,
            CodeBase = _code, CodeSize = PageSize, Entry = Entry, Wrapper = Wrapper,
            Gateway = Gateway, ForeignStub = ForeignStub, WorkerThreadId = _workerId, Heartbeat = Heartbeat
        };

        private Reply Snapshot() => new Reply
        {
            AlphaBits = Marshal.ReadInt32((IntPtr)(long)_state, 0),
            Kind = Marshal.ReadInt32((IntPtr)(long)_state, 4),
            Flags = unchecked((ulong)Marshal.ReadInt64((IntPtr)(long)_state, 8)),
            ReceivedState = unchecked((ulong)Marshal.ReadInt64((IntPtr)(long)_state, 16)),
            Counter = unchecked((ulong)Marshal.ReadInt64((IntPtr)(long)_state, 24)),
            Heartbeat = Heartbeat, GuardParked = _parked, WorkerThreadId = _workerId,
            InstructionPointer = _parked ? Entry : 0
        };

        public Reply Handle(Request request)
        {
            switch (request.Command)
            {
                case "call":
                    float result = _call((IntPtr)(long)_state, BitConverter.Int32BitsToSingle(request.AlphaBits), request.Kind, request.Flags);
                    Reply called = Snapshot();
                    called.ResultBits = BitConverter.SingleToInt32Bits(result);
                    return called;
                case "status": return Snapshot();
                case "pulse": WaitForPulse(); return Snapshot();
                case "park-guard": ParkGuard(); return Snapshot();
                case "release-guard":
                    var released = ReleaseGuard();
                    Reply reply = Snapshot();
                    reply.ResumeCount = released.ResumeCount;
                    reply.InstructionPointer = released.InstructionPointer;
                    return reply;
                default: throw new InvalidOperationException("Unknown own-fixture command.");
            }
        }

        private void WaitForPulse()
        {
            if (_parked) throw new InvalidOperationException("Cannot request a heartbeat from the deliberately parked worker.");
            ulong before = Heartbeat;
            var wait = Stopwatch.StartNew();
            while (Heartbeat == before && wait.ElapsedMilliseconds < 500) Thread.Sleep(1);
            if (Heartbeat == before) throw new InvalidOperationException("Own native worker heartbeat did not advance.");
        }

        private static IntPtr AllocateContext(out IntPtr storage)
        {
            storage = Marshal.AllocHGlobal(ContextSize + 15);
            IntPtr aligned = (IntPtr)((storage.ToInt64() + 15) & ~15L);
            Marshal.Copy(new byte[ContextSize], 0, aligned, ContextSize);
            Marshal.WriteInt32(aligned, ContextFlagsOffset, ControlContext);
            return aligned;
        }

        private void ParkGuard()
        {
            if (_parked) throw new InvalidOperationException("Own worker is already parked.");
            if (SuspendThread(_worker) == uint.MaxValue) throw new InvalidOperationException("Cannot suspend own native worker.");
            bool retainSuspension = false;
            try
            {
                Marshal.WriteInt32(_savedContext, ContextFlagsOffset, ControlContext);
                if (!GetThreadContext(_worker, _savedContext)) throw new InvalidOperationException("Cannot save own worker context.");
                long originalIp = Marshal.ReadInt64(_savedContext, RipOffset);
                if ((ulong)originalIp < WorkerCode || (ulong)originalIp >= WorkerCode + 16)
                    throw new InvalidOperationException("Own worker is outside its authored leaf loop.");
                Marshal.WriteInt64(_savedContext, RipOffset, (long)Entry);
                try
                {
                    if (!SetThreadContext(_worker, _savedContext)) throw new InvalidOperationException("Cannot park own worker at entry RIP.");
                }
                finally { Marshal.WriteInt64(_savedContext, RipOffset, originalIp); }
                _parked = true;
                retainSuspension = true;
            }
            finally { if (!retainSuspension) ResumeThread(_worker); }
        }

        private (uint ResumeCount, ulong InstructionPointer) ReleaseGuard()
        {
            if (!_parked) throw new InvalidOperationException("Own worker has no retained guard suspension.");
            IntPtr probe = AllocateContext(out IntPtr storage);
            try
            {
                if (!GetThreadContext(_worker, probe)) throw new InvalidOperationException("Cannot inspect parked own worker.");
                ulong observedIp = unchecked((ulong)Marshal.ReadInt64(probe, RipOffset));
                if (!SetThreadContext(_worker, _savedContext)) throw new InvalidOperationException("Cannot restore own worker context.");
                uint previous = ResumeThread(_worker);
                if (previous == uint.MaxValue) throw new InvalidOperationException("Cannot release own guard suspension.");
                _parked = false;
                // In a failing implementation, report the leaked count but still settle this isolated worker safely.
                uint outstanding = previous;
                for (int recovery = 0; outstanding > 1 && recovery < 8; recovery++)
                    outstanding = ResumeThread(_worker);
                WaitForPulse();
                return (previous, observedIp);
            }
            finally { Marshal.FreeHGlobal(storage); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            bool settled = _worker == IntPtr.Zero;
            try
            {
                if (_worker != IntPtr.Zero)
                {
                    try { if (_parked) ReleaseGuard(); }
                    finally
                    {
                        Marshal.WriteInt32((IntPtr)(long)_state, StopOffset, 1);
                        settled = Native.WaitForSingleObject(_worker, 1000) == 0;
                        Native.CloseHandle(_worker);
                        _worker = IntPtr.Zero;
                    }
                }
            }
            finally
            {
                if (_contextStorage != IntPtr.Zero) Marshal.FreeHGlobal(_contextStorage);
                bool freed = true;
                // Published storage is retained through every test toggle and freed only after calls/workers settle.
                if (settled)
                {
                    if (_code != 0) freed &= _self.Free(_code);
                    if (_state != 0) freed &= _self.Free(_state);
                }
                _self.Dispose();
                if (!settled || !freed)
                    throw new InvalidOperationException("Own native fixture did not settle/free cleanly; no thread or process was force-killed.");
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadContext(IntPtr thread, IntPtr context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadContext(IntPtr thread, IntPtr context);
}
