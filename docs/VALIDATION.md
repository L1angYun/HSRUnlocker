# 验证范围 / Validation Scope

更新日期：2026-10-17。本页记录当前功能的验证范围，不将构建成功、合成测试通过与游戏内效果视为同一个结论，也不保证所有场景均适用。

Updated October 17, 2026. Build results, synthetic tests, and observed in-game behavior are separate conclusions. These checks do not establish compatibility with every scene or environment.

## 功能验证 / Functional Checks

以下结果来自 Windows 11 上的已公开客户端。

The following checks used a publicly released client on Windows 11.

| 项目 / Check | 范围与结果 / Scope and Result |
|---|---|
| 启动与附加 / Launch and attach | 启动、附加及会话退出流程通过。 / Launch, attachment, and session shutdown passed. |
| 动态定位 / Dynamic resolution | 结构定位与运行时一致性校验通过；目标不由安装目录名称决定。 / Structural resolution and runtime consistency checks passed; directory names do not determine targets. |
| 帧率目标 / Frame-rate target | 目标值写入、维护及停止还原通过。未独立测量实际渲染 FPS，目标值读回不等同于渲染帧数。 / Target updates, maintenance, and restoration passed. Rendered FPS was not independently measured; reading back a target does not measure rendering performance. |
| FOV | 同一静止场景的画面对照确认视野变化，停止后还原。 / A same-scene visual comparison confirmed the change in field of view and restoration on stop. |
| 相机反虚化 / Camera anti-fade | 当前验证场景中启用有效，停用后恢复。场景覆盖并非穷尽，不能据此保证所有距离、仰角和角色过渡均具有相同表现。 / Enabling produced the intended effect in the checked scene, and disabling restored normal behavior. Scene coverage is not exhaustive and does not establish identical behavior at every distance, angle, or character transition. |
| 移动 UI / Mobile UI | 启动进入触控布局，画质设置来源的选择与保存通过。 / Launching into the touch layout and selecting and saving the graphics-settings source passed. |

## 界面与构建 / UI and Build

原生 WinUI 3 界面在 Windows 11、150% 显示缩放下完成检查；该组界面检查不启动或附加游戏。

The native WinUI 3 interface was checked on Windows 11 at 150% display scaling, without launching or attaching to a game.

- 已检查宽窗口双栏、窄窗口单栏滚动、底部操作栏与图标显示；其他 DPI、主题和显示器组合未逐一验证。 / Wide two-column and narrow scrollable layouts, the action bar, and icon rendering were checked. Other DPI, theme, and monitor combinations were not exhaustively checked.
- 简体中文、English、日本語、한국어 的即时切换、跟随系统、选择保存及重启恢复通过；切换过程中其余配置、FPS/FOV 数值与六个预设保持不变。 / Switching among Simplified Chinese, English, Japanese, and Korean, following the system language, saving the choice, and restoring it on restart passed. Other settings, FPS/FOV values, and the six presets remained unchanged while switching.
- 数值框、滑块、预设、重置、开关和画质来源的状态同步通过。 / Numeric inputs, sliders, presets, reset actions, switches, and graphics-source state synchronization passed.
- 日志展开、清空和按钮状态通过；复制按钮的剪贴板写入未在该组检查中执行。 / Log expansion, clearing, and button availability passed; clipboard writing was not exercised in these checks.
- 托盘最小化、重复启动恢复已有窗口、正常退出及验证前配置恢复通过。 / Tray minimization, restoring the existing window on duplicate launch, normal shutdown, and restoration of the original settings passed.
- 发布构建及源码包解包构建通过，编译 XAML 资源、图标、MiSans 原字体、字体版权通知及完整许可包含在输出中。 / Publishing and building an extracted source package passed, with compiled XAML resources, icons, the unmodified MiSans font, its copyright notice, and the complete font license present in the output.
- 最终字体样式通过构建与静态审查，未完成该次样式调整后的原生界面复查；不将资源存在或构建成功等同于运行时字形验证。 / The final font styles passed compilation and static review; the native interface was not rechecked after that style adjustment. Resource presence and build success do not establish runtime glyph rendering.

## 安装与更新 / Installation and Updates

1.1.0 自包含发布及 Inno Setup 安装程序编译通过。安装包解出 **551 个应用文件**，逐文件 SHA-256 与发布目录一致，无缺失、多余或 PDB 文件；四种安装语言、编译 XAML、.NET 与 Windows App SDK 运行组件、MiSans 和许可通知均已检查。

The 1.1.0 self-contained publish and Inno Setup installer compiled successfully. All **551 application files** extracted from the installer matched the publish directory by SHA-256, with no missing, extra, or PDB files. Checks covered the four installer languages, compiled XAML, .NET and Windows App SDK runtime components, MiSans, and license notices.

更新检查、下载、长度与 SHA-256 校验、取消、失败清理和交接命令边界使用自编 HTTP 响应与临时文件验证。安装保护使用本测试自建的随机全局互斥体，验证可见性、多个持有者及最后关闭后的释放；未使用真实应用的互斥体。

Update checks, downloads, length and SHA-256 verification, cancellation, failure cleanup, and handoff command boundaries used authored HTTP responses and temporary files. Installer-guard checks used an owned, randomly named global mutex to verify visibility, multiple holders, and release after the final close; they did not use the application's actual mutex.

本轮未运行安装包或更新助手，未触发 UAC，也未访问真实配置或启动游戏。生产环境的受保护下载目录、助手就绪握手、安装目录沿用、跨用户会话升级保护、卸载和升级后重启尚未进行完整运行验证。新增更新区、退出开关及调度器倒计时通过编译和代码审查，未完成原生界面操作复查。

This run did not execute the installer or update helper, trigger UAC, read live settings, or launch a game. The production protected download directory, helper readiness handshake, reuse of an existing installation directory, upgrade protection across user sessions, uninstallation, and restart after upgrading have not been exercised end to end. The new update controls, exit switch, and dispatcher countdown passed compilation and code review, without a native UI interaction recheck.

## 合成测试 / Synthetic Tests

2026-10-17 的 1.1.0 测试运行通过 **458 项检查**。具体数量以对应源码版本的运行输出为准。

The October 17, 2026 run for 1.1.0 passed **458 checks**. The output for the corresponding source revision is authoritative.

`tests/HSRUnlocker.Tests` 使用合成 PE、固定测试数据、临时目录和自有内存，不读取已安装游戏、真实设置或注册表，不联网，也不启动游戏。原生调用与展开测试会执行本项目自行编写的合成机器码，并使用一个有界的同程序集测试子进程。

`tests/HSRUnlocker.Tests` uses synthetic PE files, authored fixtures, temporary directories, and owned memory. It does not read installed games, live settings, or the registry, access the network, or launch a game. Native calling-convention and unwind checks execute synthetic machine code authored for this project and use one bounded child running the same test assembly.

覆盖范围包括结构解析与扫描、动态定位、无效或歧义结构拒绝、调用约定、并发与还原、移动平台参数、配置归一化和权限检查；语言测试覆盖系统语言选择、手动选择与配置兼容、四语资源及格式参数、数字格式和并发切换。合成测试通过不替代真实环境中的功能观察。

Coverage includes parsing and scanning, dynamic resolution, rejection of invalid or ambiguous structures, calling conventions, concurrency and restoration, mobile-platform arguments, settings normalization, and privilege checks. Localization checks cover system and manual selection, settings compatibility, four-language catalogs and format values, numeric formatting, and concurrent language changes. Synthetic tests do not replace observing behavior in the intended environment.

1.1.0 另覆盖旧配置默认值、新开关的保存与克隆、正式版本比较、固定仓库与安装包选择、响应大小和超时边界、下载进度、损坏或截断数据拒绝、取消与占用文件清理，以及安装保护的原生句柄生命周期。

Additional 1.1.0 coverage includes legacy preference defaults, persistence and cloning of the new switches, stable version ordering, fixed repository and installer selection, response size and timeout bounds, download progress, rejection of corrupt or truncated data, cancellation and locked-file cleanup, and native handle lifetime for the installer guard.

`tests/Packaging.Tests.ps1` 的 **20 项打包检查**使用临时自编 NuGet 包，覆盖许可与通知原样复制、框架下载依赖、中文路径、隐私清单和越界路径拒绝，不读取真实游戏或配置。

The **20 packaging checks** in `tests/Packaging.Tests.ps1` use temporary authored NuGet packages. They cover verbatim license and notice copying, framework download dependencies, Unicode paths, manifest privacy, and rejection of unsafe paths, without reading a game or live settings.

## 本地复现 / Local Checks

```powershell
dotnet run --project .\tests\HSRUnlocker.Tests\HSRUnlocker.Tests.csproj -c Release
powershell -NoProfile -File .\tests\Packaging.Tests.ps1
powershell -NoProfile -File .\build.ps1 -OutDir .\artifacts\win-x64
# Inno Setup 7.1+
powershell -NoProfile -File .\build-installer.ps1 -OutDir .\artifacts\installer
```

发布目录必须为空或不存在；构建脚本不启动应用或游戏。只向仓库提交有权公开的源码、合成测试和脱敏文字说明，不提交游戏文件、资源、截图、转储、原始日志或私人配置。

Publish into an empty or nonexistent directory; the build script does not launch the application or game. Submit only source, synthetic tests, and sanitized text that you have the right to publish. Do not submit game files, assets, screenshots, dumps, raw logs, or private settings.
