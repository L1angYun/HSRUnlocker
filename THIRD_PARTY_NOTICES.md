# 第三方组件与许可

本项目自行编写的源码以 **GPL-3.0-only** 发布，见 [LICENSE](LICENSE)。第三方组件遵循各自的许可，不因被本项目引用而改用 GPL。

本源码仓库不包含依赖项二进制文件；构建时由 NuGet 还原所需包。源码仓库与自行构建的发布目录不是同一种分发内容。

## 构建依赖

| 包 | 版本 | 用途 | 许可来源 |
|---|---|---|---|
| `Microsoft.WindowsAppSDK` | 2.5.1 | WinUI 3 与 Windows App SDK | 包内 `license.txt` 中的 Microsoft Software License Terms，不是 MIT |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.26100.4654 | Windows SDK 构建工具 | 包元数据所指的 Microsoft SDK 许可条款 |

Windows App SDK 会引入多个子包。实际依赖与版本以还原后的 `project.assets.json` 为准，各组件附带的许可、版权和第三方通知仍适用。

## 运行时

- .NET 8 运行时；本项目不要求 Windows Desktop Runtime。具体许可和第三方通知以所用 .NET 发行版为准。
- Windows App Runtime / Windows App SDK 组件，适用其随附许可与通知。
- 由 Windows 提供的系统 DLL，不随本源码仓库分发。

## 二进制分发

构建后的发布目录可能包含依赖项二进制文件。分发者须逐项核对实际包含的组件，保留适用的许可、版权与第三方通知，并满足各组件的再分发条件。Windows App SDK 的可分发代码还受其许可中的分发要求与限制约束，不能仅按本项目的 GPL 许可处理。

`build.ps1` 根据 NuGet 还原清单中的包与框架下载依赖，从实际包目录读取许可元数据，并将已有许可和通知文本原样复制到 `ThirdPartyNotices/<包名>/<版本>/`，保留原相对目录。`ThirdPartyNotices/manifest.json` 记录许可来源、已复制文件及没有内置文本的包，不记录本机缓存路径。只有许可链接或表达式不等于已经附带许可原文；还原清单未列出的 SDK、系统组件或自包含运行时包仍须单独复核。

本文件提供组件与许可来源说明，不对不同组件的许可兼容性或具体分发方式作法律结论。

## 图标与其他材料

应用图标由项目维护者提供，不取自游戏资源。本说明不代替任何第三方素材自身的许可或授权。

游戏客户端、游戏资源、游戏截图、第三方程序载荷、反编译输出、原始日志与内存转储不属于本项目的分发内容，也不因本项目的源码许可获得使用或再分发授权。
