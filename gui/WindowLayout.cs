using System;
using System.Runtime.InteropServices;

namespace StarRailUnlocker
{
    internal sealed class WindowLayout : IDisposable
    {
        private const nuint SubclassId = 0x4853524C;
        private readonly IntPtr _window;
        private readonly int _minimumWidth, _minimumHeight;
        private readonly SubclassProc _callback;
        private bool _disposed;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr SubclassProc(IntPtr window, uint message, nuint wparam, nint lparam, nuint id, nuint data);
        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
        [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, nuint id, nuint data);
        [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, nuint id);
        [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, nuint wparam, nint lparam);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);

        internal WindowLayout(IntPtr window, int minimumWidthDip = 520, int minimumHeightDip = 480)
        {
            if (minimumWidthDip <= 0) throw new ArgumentOutOfRangeException(nameof(minimumWidthDip));
            if (minimumHeightDip <= 0) throw new ArgumentOutOfRangeException(nameof(minimumHeightDip));
            _window = window;
            _minimumWidth = minimumWidthDip; _minimumHeight = minimumHeightDip;
            _callback = OnMessage;
            if (!SetWindowSubclass(window, _callback, SubclassId, 0))
                throw new InvalidOperationException("Cannot register the HSRUnlocker window sizing callback");
        }

        internal static double ScaleForWindow(IntPtr window)
        {
            try { uint dpi = GetDpiForWindow(window); return (dpi == 0 ? 96 : dpi) / 96.0; }
            catch { return 1.0; }
        }

        private IntPtr OnMessage(IntPtr window, uint message, nuint wparam, nint lparam, nuint id, nuint data)
        {
            try
            {
                IntPtr result = DefSubclassProc(window, message, wparam, lparam);
                if (message == 0x24 && lparam != 0)
                {
                    var info = Marshal.PtrToStructure<MinMaxInfo>(lparam);
                    double scale = ScaleForWindow(window);
                    info.MinTrackSize.X = Math.Max(info.MinTrackSize.X, (int)Math.Ceiling(_minimumWidth * scale));
                    info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y, (int)Math.Ceiling(_minimumHeight * scale));
                    Marshal.StructureToPtr(info, lparam, false);
                }
                return result;
            }
            catch (Exception ex) { App.WriteLog("Window sizing callback", ex); return IntPtr.Zero; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            RemoveWindowSubclass(_window, _callback, SubclassId);
            _disposed = true;
            GC.KeepAlive(_callback);
        }
    }
}
