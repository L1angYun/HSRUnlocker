using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace StarRailUnlocker.Core
{
    public enum MobileGraphicsSource
    {
        Auto = 0,
        China = 1,
        Global = 2,
    }

    /// <summary>Resolve the selected client's graphics identity without modifying PlayerPrefs.</summary>
    public static class MobileLaunchSettings
    {
        public const string GraphicsValueName = "GraphicsSettings_Model_h2986158309";
        public const string ChinaRegistryPath = @"SOFTWARE\miHoYo\崩坏：星穹铁道";
        public const string GlobalRegistryPath = @"SOFTWARE\Cognosphere\Star Rail";

        public static IReadOnlyList<string> BuildArguments(string executable, MobileGraphicsSource source,
            Action<string> log = null) => BuildArguments(executable, source, ReadOptionalLines, ReadRegistryValue, log);

        // These two read-only boundaries let verification use its own fixtures, never live PlayerPrefs.
        internal static IReadOnlyList<string> BuildArguments(string executable, MobileGraphicsSource source,
            Func<string, string[]> readLines, Func<string, string, object> readRegistry, Action<string> log = null)
        {
            ArgumentNullException.ThrowIfNull(readLines);
            ArgumentNullException.ThrowIfNull(readRegistry);
            if (!Enum.IsDefined(source))
                throw new InvalidOperationException("移动 UI 图形设置来源无效；请选择自动、国服或国际服");

            MobileGraphicsSource resolved = source;
            if (source == MobileGraphicsSource.Auto)
            {
                string directory = Path.GetDirectoryName(executable) ?? string.Empty;
                string dataDirectory = Path.GetFileNameWithoutExtension(executable) + "_Data";
                resolved = ResolveSource(source, readLines(Path.Combine(directory, "config.ini")),
                    readLines(Path.Combine(directory, dataDirectory, "app.info")));
            }

            string registryPath = RegistryPath(resolved);
            log?.Invoke($"移动 UI 图形设置：{(source == MobileGraphicsSource.Auto ? "自动识别" : "手动选择")} " +
                        $"{(resolved == MobileGraphicsSource.China ? "国服" : "国际服")}；只读 HKCU\\{registryPath}");
            if (readRegistry(registryPath, GraphicsValueName) is not byte[] graphics || graphics.Length == 0)
                throw new InvalidOperationException($"移动 UI 启动需要所选{(resolved == MobileGraphicsSource.China ? "国服" : "国际服")}的图形设置" +
                    $"（HKCU\\{registryPath}）；请先用普通模式启动相应客户端，再启用移动 UI");

            log?.Invoke($"使用移动 UI 启动参数 CLOUD_WEB_TOUCH；图形设置 {graphics.Length} 字节，客户端效果待确认");
            return new[] { "-platform_type", "CLOUD_WEB_TOUCH", "-graphics_setting", Convert.ToBase64String(graphics) };
        }

        internal static MobileGraphicsSource ResolveSource(MobileGraphicsSource requested,
            IReadOnlyList<string> iniLines, IReadOnlyList<string> appInfoLines)
        {
            if (!Enum.IsDefined(requested))
                throw new InvalidOperationException("移动 UI 图形设置来源无效");
            // A manual choice is an explicit override; it does not consult another installation's files.
            if (requested != MobileGraphicsSource.Auto) return requested;

            MobileGraphicsSource channel = ReadChannelIdentity(iniLines);
            MobileGraphicsSource metadata = ReadAppIdentity(appInfoLines);
            if (channel != MobileGraphicsSource.Auto && metadata != MobileGraphicsSource.Auto && channel != metadata)
                throw new InvalidOperationException("当前客户端 config.ini 与 app.info 的移动 UI 渠道信息冲突；请确认客户端或手动选择图形设置来源");
            if (channel != MobileGraphicsSource.Auto) return channel;
            if (metadata != MobileGraphicsSource.Auto) return metadata;
            throw new InvalidOperationException("无法从当前客户端 config.ini（uapc）或 StarRail_Data/app.info 确认移动 UI 渠道；请手动选择国服或国际服图形设置来源");
        }

        internal static string RegistryPath(MobileGraphicsSource source) => source switch
        {
            MobileGraphicsSource.China => ChinaRegistryPath,
            MobileGraphicsSource.Global => GlobalRegistryPath,
            _ => throw new InvalidOperationException("尚未确认移动 UI 图形设置渠道"),
        };

        private static MobileGraphicsSource ReadChannelIdentity(IReadOnlyList<string> lines)
        {
            MobileGraphicsSource result = MobileGraphicsSource.Auto;
            if (lines == null) return result;
            foreach (string raw in lines)
            {
                string line = Clean(raw);
                int separator = line.IndexOf('=');
                if (separator < 0 || !line[..separator].Trim().Equals("uapc", StringComparison.OrdinalIgnoreCase))
                    continue;
                string value = line[(separator + 1)..].Trim();
                if (value.Length == 0) continue;
                MobileGraphicsSource next = ParseChannel(value);
                if (result != MobileGraphicsSource.Auto && result != next)
                    throw new InvalidOperationException("当前客户端 config.ini 包含互相冲突的 uapc 渠道；请确认客户端或手动选择图形设置来源");
                result = next;
            }
            return result;
        }

        private static MobileGraphicsSource ParseChannel(string value)
        {
            if (value.Equals("hkrpg_cn", StringComparison.OrdinalIgnoreCase)) return MobileGraphicsSource.China;
            if (value.Equals("hkrpg_global", StringComparison.OrdinalIgnoreCase)) return MobileGraphicsSource.Global;
            try
            {
                using var document = JsonDocument.Parse(value);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    bool china = false, global = false;
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        china |= property.Name.Equals("hkrpg_cn", StringComparison.OrdinalIgnoreCase);
                        global |= property.Name.Equals("hkrpg_global", StringComparison.OrdinalIgnoreCase);
                    }
                    if (china && global)
                        throw new InvalidOperationException("当前客户端 uapc 同时包含国服和国际服渠道；请确认客户端或手动选择图形设置来源");
                    if (china) return MobileGraphicsSource.China;
                    if (global) return MobileGraphicsSource.Global;
                }
            }
            catch (JsonException)
            {
                // Unknown or malformed launcher metadata must not imply a CN client.
            }
            throw new InvalidOperationException("当前客户端 config.ini 的 uapc 渠道无法识别；请确认客户端或手动选择图形设置来源");
        }

        private static MobileGraphicsSource ReadAppIdentity(IReadOnlyList<string> lines)
        {
            if (lines == null) return MobileGraphicsSource.Auto;
            var identity = new List<string>(2);
            foreach (string raw in lines)
            {
                string line = Clean(raw);
                if (line.Length > 0) identity.Add(line);
            }
            if (identity.Count == 0) return MobileGraphicsSource.Auto;
            if (identity.Count == 2)
            {
                if (identity[0].Equals("miHoYo", StringComparison.OrdinalIgnoreCase) && identity[1] == "崩坏：星穹铁道")
                    return MobileGraphicsSource.China;
                if (identity[0].Equals("Cognosphere", StringComparison.OrdinalIgnoreCase) &&
                    identity[1].Equals("Star Rail", StringComparison.OrdinalIgnoreCase))
                    return MobileGraphicsSource.Global;
            }
            throw new InvalidOperationException("当前客户端 app.info 的公司/产品信息无法识别；请确认客户端或手动选择图形设置来源");
        }

        private static string Clean(string value) => (value ?? string.Empty).Trim().TrimStart('\uFEFF').Trim();

        internal const int MaxConfigMetadataBytes = 64 * 1024;
        internal const int MaxAppInfoBytes = 4 * 1024;

        // Read at most the limit plus one sentinel byte, including if the file grows while open.
        // Missing metadata is optional; malformed, unreadable or oversized metadata is refused.
        internal static string[] ReadOptionalLines(string path)
        {
            int limit = Path.GetFileName(path).Equals("app.info", StringComparison.OrdinalIgnoreCase)
                ? MaxAppInfoBytes : MaxConfigMetadataBytes;
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }

            using (stream)
            {
                byte[] bytes = new byte[limit + 1];
                int length = 0;
                while (length < bytes.Length)
                {
                    int count = stream.Read(bytes, length, bytes.Length - length);
                    if (count == 0) break;
                    length += count;
                }
                if (length > limit)
                    throw new InvalidOperationException($"当前客户端 {Path.GetFileName(path)} 元数据超过 {limit} 字节；请确认客户端或手动选择图形设置来源");

                using var content = new MemoryStream(bytes, 0, length, writable: false);
                using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var lines = new List<string>();
                string line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
                return lines.ToArray();
            }
        }

        private static object ReadRegistryValue(string path, string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
    }
}
