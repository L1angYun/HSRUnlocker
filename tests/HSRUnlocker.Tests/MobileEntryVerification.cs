using System.Runtime.InteropServices;
using System.Text;
using StarRailUnlocker.Core;

internal static class MobileEntryVerification
{
    public static void Run(Action<bool, string> check)
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"FPS\":60}\0"));
        var arguments = new[] { "-platform_type", "CLOUD_WEB_TOUCH", "-graphics_setting", payload };
        check(MobileGameLauncher.BuildCommandLine(arguments) == string.Join(" ", arguments),
            "mobile entry preserves exact canonical graphics bytes and raw platform arguments");
        foreach (var bad in new[] { Array.Empty<string>(), new[] { "-platform_type", "CLOUD_WEB_TOUCH", "-graphics_setting", "" },
            new[] { "-platform_type", "CLOUD_WEB_TOUCH", "-graphics_setting", "YQ== x" } })
        {
            bool refused = false;
            try { MobileGameLauncher.BuildCommandLine(bad); } catch (ArgumentException) { refused = true; }
            check(refused, "mobile entry rejects malformed or empty raw arguments before creating a process");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            bool stopped = false;
            try { MobileGameLauncher.Launch("invalid", Array.Empty<string>(), cancellationToken: cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            check(stopped, "cancelled mobile launch stops before touching files or creating a process");
        }
        using var process = new TargetProcess((uint)Environment.ProcessId);
        ulong region = process.Allocate(0x3000);
        if (region == 0) throw new InvalidOperationException("Own fixture allocation failed");
        IntPtr thread = IntPtr.Zero;
        bool finished = true;
        try
        {
            ulong wrapper = region + 0x1000, entry = region + 0x2000, command = region + 128;
            // Authored native fixture records Win64 entry arguments and callee stack alignment.
            byte[] stub = { 0x48,0x89,0x4B,0x60, 0x48,0x89,0x53,0x68, 0x4C,0x89,0x43,0x70,
                0x44,0x89,0x4B,0x78, 0x48,0x89,0xE0, 0x83,0xE0,0x0F, 0x89,0x43,0x7C,
                0xB8,0x25,0,0,0, 0xC3 };
            byte[] state = new byte[512];
            BitConverter.GetBytes(0x123456789ABCUL).CopyTo(state, 8);
            BitConverter.GetBytes(command).CopyTo(state, 16);
            BitConverter.GetBytes(entry).CopyTo(state, 24);
            Encoding.Unicode.GetBytes("-platform_type CLOUD_WEB_TOUCH\0").CopyTo(state, 128);
            check(process.WriteData(region, state) && process.WriteData(wrapper, MobileGameLauncher.BuildBootstrap())
                && process.WriteData(entry, stub) && process.WriteData(entry + 0x100, new byte[] { 0x89,0x4B,0x58,0xC3 })
                && process.MakeExecutable(wrapper, 0x2000),
                "mobile fixture separates writable state and read-execute bootstrap/entry pages");
            thread = Native.CreateRemoteThread(process.Handle, IntPtr.Zero, UIntPtr.Zero,
                (IntPtr)(long)wrapper, (IntPtr)(long)region, 0, out _);
            if (thread == IntPtr.Zero) throw new InvalidOperationException("Own bootstrap thread creation failed");
            finished = false;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (process.ReadInt32(region + 4) != 1 && DateTime.UtcNow < deadline) Thread.Sleep(1);
            check(process.ReadInt32(region + 4) == 1 && process.ReadInt32(region + 32) == 0
                && Native.WaitForSingleObject(thread, 0) == 258,
                "bootstrap waits at gate without calling the entry before validation");
            check(process.WriteInt32(region, 1), "release the own-process entry after verification");
            finished = Native.WaitForSingleObject(thread, 3000) == 0;
            check(finished && process.ReadInt32(region + 4) == 3 && process.ReadInt32(region + 32) == 37,
                "bootstrap forwards the entry result and reports a returned entry");
            check(process.ReadUInt64(region + 96) == 0x123456789ABCUL && process.ReadUInt64(region + 104) == 0
                && process.ReadUInt64(region + 112) == command && process.ReadInt32(region + 120) == 10,
                "Unity entry receives HINSTANCE, null previous instance, UTF-16 command pointer and SW_SHOWDEFAULT");
            check(process.ReadInt32(region + 124) == 8, "entry sees Win64 stack alignment and caller shadow space");
            Native.CloseHandle(thread); thread = IntPtr.Zero;
            process.WriteData(region + 40, BitConverter.GetBytes(entry + 0x100));
            process.WriteInt32(region + 4, 0); process.WriteInt32(region + 88, 0);
            thread = Native.CreateRemoteThread(process.Handle, IntPtr.Zero, UIntPtr.Zero,
                (IntPtr)(long)wrapper, (IntPtr)(long)region, 0, out _);
            if (thread == IntPtr.Zero) throw new InvalidOperationException("Own exit fixture thread creation failed");
            finished = false;
            finished = Native.WaitForSingleObject(thread, 3000) == 0;
            check(finished && process.ReadInt32(region + 4) == 3 && process.ReadInt32(region + 88) == 37,
                "returned Unity entry calls its configured exit continuation with the original result");
            Native.CloseHandle(thread); thread = IntPtr.Zero;
            process.WriteInt32(region, 2); process.WriteInt32(region + 4, 0); process.WriteInt32(region + 32, 0);
            thread = Native.CreateRemoteThread(process.Handle, IntPtr.Zero, UIntPtr.Zero,
                (IntPtr)(long)wrapper, (IntPtr)(long)region, 0, out _);
            if (thread == IntPtr.Zero) throw new InvalidOperationException("Own cancel thread creation failed");
            finished = false;
            finished = Native.WaitForSingleObject(thread, 3000) == 0;
            check(finished && process.ReadInt32(region + 4) == 1 && process.ReadInt32(region + 32) == 0,
                "cancelled bootstrap returns without calling the entry");
        }
        finally
        {
            if (!finished) { process.WriteInt32(region, 2); finished = Native.WaitForSingleObject(thread, 3000) == 0; }
            if (thread != IntPtr.Zero) Native.CloseHandle(thread);
            if (finished) check(process.Free(region), "free mobile fixture only after its own threads have stopped");
        }
    }
}
