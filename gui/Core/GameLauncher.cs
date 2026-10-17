using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace StarRailUnlocker.Core
{
    /// <summary>Launch options for StarRail, including experimental mobile parameters.</summary>
    public static class GameLauncher
    {
        public static IReadOnlyList<string> MobileArguments(string executable, MobileGraphicsSource source, Action<string> log = null) =>
            MobileLaunchSettings.BuildArguments(executable, source, log);

        public static uint Launch(UnlockConfig config, Action<string> log = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(config.ProcessPath) || !File.Exists(config.ProcessPath)
                || !Path.GetFileName(config.ProcessPath).Equals("StarRail.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择有效的 StarRail.exe 游戏路径");
            string compatibility = Environment.GetEnvironmentVariable("__COMPAT_LAYER") ?? "";
            if (compatibility.Contains("RunAsInvoker", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("检测到 RunAsInvoker 降权启动环境，已取消启动游戏；请从正常管理员权限的 HSRUnlocker 启动，避免资源更新失败");
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("启动游戏需要管理员权限以写入客户端资源目录；请正常运行 HSRUnlocker 并允许 Windows 管理员提示");
            if (config.UseMobileUI)
                return MobileGameLauncher.Launch(config.ProcessPath,
                    MobileArguments(config.ProcessPath, config.GraphicsSource, log), log, cancellationToken);
            var start = new ProcessStartInfo(config.ProcessPath)
            {
                WorkingDirectory = Path.GetDirectoryName(config.ProcessPath),
                UseShellExecute = false,
            };
            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(start) ?? throw new InvalidOperationException("游戏未能启动");
            return checked((uint)process.Id);
        }
    }
}
