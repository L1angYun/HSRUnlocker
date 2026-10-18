# 第三方组件与许可 / Third-Party Notices

[简体中文](#简体中文) | [English](#english)

## 简体中文

本项目自行编写的源码以 **GPL-3.0-only** 发布，见 [LICENSE](LICENSE)。第三方组件遵循各自的许可，不因被本项目引用而改用 GPL。

构建依赖由 NuGet 还原；仓库中另附有作为应用资源使用的 MiSans 字体及其许可。源码仓库与自行构建的发布目录不是同一种分发内容。

### MiSans 字体

界面使用随软件附带的原版 MiSans VF 字体。Copyright © 2020-2023 Beijing Xiaomi Mobile Software Co.,Ltd. All Rights Reserved.

字体文件、版权说明和完整小米字体许可分别位于 `gui/Assets/Fonts/MiSansVF.ttf`、[字体通知](gui/Assets/Fonts/NOTICE.md) 和 [字体许可](gui/Assets/Fonts/MiSans-LICENSE.pdf)，构建时一并复制到发布目录的 `Assets/Fonts/`。字体遵循小米的独立许可，不适用本项目源码的 GPL 许可；随软件分发时须保留原字体、版权及许可，遵守其署名和分发条件。

MiSans 未包含的字形使用 Windows 字体回退；这些系统字体不随软件分发。

### 构建依赖

| 包 | 版本 | 用途 | 许可来源 |
|---|---|---|---|
| `Microsoft.WindowsAppSDK` | 2.5.1 | WinUI 3 与 Windows App SDK | 包内 `license.txt` 中的 Microsoft Software License Terms，不是 MIT |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.26100.4654 | Windows SDK 构建工具 | 包元数据所指的 Microsoft SDK 许可条款 |

Windows App SDK 会引入多个子包。实际依赖与版本以还原后的 `project.assets.json` 为准，各组件附带的许可、版权和第三方通知仍适用。

### 运行时

- .NET 8 运行时；本项目不要求 Windows Desktop Runtime。具体许可和第三方通知以所用 .NET 发行版为准。
- Windows App Runtime / Windows App SDK 组件，适用其随附许可与通知。
- 由 Windows 提供的系统 DLL，不随本源码仓库分发。

### 二进制分发

构建后的发布目录可能包含依赖项二进制文件。分发者须逐项核对实际包含的组件，保留适用的许可、版权与第三方通知，并满足各组件的再分发条件。Windows App SDK 的可分发代码还受其许可中的分发要求与限制约束，不能仅按本项目的 GPL 许可处理。

`build.ps1` 根据 NuGet 还原清单中的包与框架下载依赖，从实际包目录读取许可元数据，并将已有许可和通知文本原样复制到 `ThirdPartyNotices/<包名>/<版本>/`，保留原相对目录。`ThirdPartyNotices/manifest.json` 记录许可来源、已复制文件及没有内置文本的包，不记录本机缓存路径。只有许可链接或表达式不等于已经附带许可原文；还原清单未列出的 SDK、系统组件或自包含运行时包仍须单独复核。

本文件提供组件与许可来源说明，不对不同组件的许可兼容性或具体分发方式作法律结论。

### 图标与其他材料

应用图标由项目维护者提供，不取自游戏资源。本说明不代替任何第三方素材自身的许可或授权。

游戏客户端、游戏资源、游戏截图、第三方程序载荷、反编译输出、原始日志与内存转储不属于本项目的分发内容，也不因本项目的源码许可获得使用或再分发授权。

## English

The source authored for this project is released under **GPL-3.0-only**; see [LICENSE](LICENSE). Third-party components retain their own licenses and do not become GPL-licensed merely because this project uses them.

Build dependencies are restored through NuGet. The repository also includes the MiSans font and its license as application resources. The source repository and a built distribution contain different materials.

### MiSans Font

The interface uses the bundled, unmodified MiSans VF font. Copyright © 2020-2023 Beijing Xiaomi Mobile Software Co.,Ltd. All Rights Reserved.

The font, copyright notice, and complete Xiaomi font license are located at `gui/Assets/Fonts/MiSansVF.ttf`, [the font notice](gui/Assets/Fonts/NOTICE.md), and [the font license](gui/Assets/Fonts/MiSans-LICENSE.pdf). The build copies them into `Assets/Fonts/` in the publish directory. Xiaomi's separate font license applies, not the GPL license for this project's source. Preserve the original font, copyright notice, and license, and comply with their attribution and distribution conditions.

Windows font fallback supplies glyphs not included in MiSans. Those system fonts are not distributed with this software.

### Build Dependencies

| Package | Version | Purpose | License Source |
|---|---|---|---|
| `Microsoft.WindowsAppSDK` | 2.5.1 | WinUI 3 and Windows App SDK | Microsoft Software License Terms in the package's `license.txt`, not MIT |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.26100.4654 | Windows SDK build tools | Microsoft SDK terms referenced by the package metadata |

Windows App SDK brings in multiple subpackages. The restored `project.assets.json` is authoritative for resolved dependencies and versions. Each component's accompanying licenses, copyright notices, and third-party notices continue to apply.

### Runtimes

- The .NET 8 runtime; this project does not require Windows Desktop Runtime. The applicable licenses and third-party notices depend on the .NET distribution used.
- Windows App Runtime / Windows App SDK components, under their accompanying licenses and notices.
- System DLLs supplied by Windows, which are not distributed in this source repository.

### Binary Distribution

A built distribution may contain dependency binaries. Distributors must review the components actually included, retain applicable licenses, copyright notices, and third-party notices, and satisfy each component's redistribution conditions. Windows App SDK redistributable code is also subject to its license's distribution requirements and restrictions; this project's GPL license alone is not sufficient.

Using the NuGet restore inventory, `build.ps1` reads license metadata from the actual package directories for packages and framework download dependencies. It copies available license and notice texts verbatim to `ThirdPartyNotices/<package>/<version>/`, preserving their relative paths. `ThirdPartyNotices/manifest.json` records license sources, copied files, and packages without embedded texts, without recording local cache paths. A license link or expression does not mean the complete license text is included. SDKs, system components, and self-contained runtime packages not listed in the restore inventory require separate review.

This document identifies components and license sources. It does not make legal conclusions about license compatibility or a particular distribution method.

### Icons And Other Materials

The application icon was supplied by the project maintainer and was not taken from game assets. This notice does not replace any third-party material's own license or permission.

Game clients, game assets, game screenshots, third-party program payloads, decompiled output, raw logs, and memory dumps are not part of this project's distribution. The source license does not grant permission to use or redistribute them.
