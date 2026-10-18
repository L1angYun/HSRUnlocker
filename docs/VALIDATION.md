# 验证范围 / Validation Scope

更新日期：2026-10-18。本页记录当前功能的验证范围，不将构建成功、合成测试通过与游戏内效果视为同一个结论，也不保证所有场景均适用。

Updated October 18, 2026. Build results, synthetic tests, and observed in-game behavior are separate conclusions. These checks do not establish compatibility with every scene or environment.

## 功能验证 / Functional Checks

下表为此前在 Windows 11 上对已公开客户端的观察。本轮自动检查未启动游戏；1.3.1 与 1.3.2 的用户实测结果单列于表后。

The table records earlier observations using a publicly released client on Windows 11. Automated checks in this run did not launch a game; user feedback for 1.3.1 and 1.3.2 is recorded separately below the table.

| 项目 / Check | 范围与结果 / Scope and Result |
|---|---|
| 启动与附加 / Launch and attach | 启动、附加及会话退出流程通过。 / Launch, attachment, and session shutdown passed. |
| 动态定位 / Dynamic resolution | 结构定位与运行时一致性校验通过；目标不由安装目录名称决定。 / Structural resolution and runtime consistency checks passed; directory names do not determine targets. |
| 帧率目标 / Frame-rate target | 目标值写入、维护及停止还原通过。未独立测量实际渲染 FPS，目标值读回不等同于渲染帧数。 / Target updates, maintenance, and restoration passed. Rendered FPS was not independently measured; reading back a target does not measure rendering performance. |
| FOV | 同一静止场景的画面对照确认视野变化，停止后还原。 / A same-scene visual comparison confirmed the change in field of view and restoration on stop. |
| 相机反虚化 / Camera anti-fade | 当前验证场景中启用有效，停用后恢复。场景覆盖并非穷尽，不能据此保证所有距离、仰角和角色过渡均具有相同表现。 / Enabling produced the intended effect in the checked scene, and disabling restored normal behavior. Scene coverage is not exhaustive and does not establish identical behavior at every distance, angle, or character transition. |
| 移动 UI / Mobile UI | 启动进入触控布局，画质设置来源的选择与保存通过。 / Launching into the touch layout and selecting and saving the graphics-settings source passed. |

1.3.0 收到移动 UI 画质降低的用户反馈。1.3.1 本地候选将移动启动使用的平台标识由 Android (11) 改为 WindowsEditor (7)，保持启动参数和画质设置原始字节不变。该修改仍影响所有读取此平台标识的代码；合成测试只确认返回值，不证明游戏内画质恢复或触控布局保持，也不覆盖 iOS 安全区。

A user reported reduced graphics quality with mobile UI in 1.3.0. The local 1.3.1 candidate changes the mobile-launch platform identity from Android (11) to WindowsEditor (7), leaving the launch arguments and raw graphics-settings bytes unchanged. This still affects every consumer of that platform identity. Synthetic checks establish only the return value, not restored in-game quality, retained touch layout, or iOS safe-area behavior.

2026-10-18，用户实测确认 1.3.1 移动 UI 的画质已恢复正常。此结果来自用户实际运行反馈，本轮未独立进行量化画质对比；iOS 安全区不在此次确认范围。

On October 18, 2026, the user confirmed normal graphics quality with mobile UI in 1.3.1 after testing it in-game. This is user-reported verification, not an independently measured graphics comparison in this run; iOS safe-area behavior is outside this confirmation.

1.3.2 增加默认「原样」及可选「iOS / 自定义」安全区。自定义范围为左右各侧 0–15%，初始值 5%，在下次由工具启动移动 UI 时应用；不将平台标识改为 iOS，也不改写画质数据、启动参数或游戏保存的偏好。实现按名称解析安全区接口，调用原接口后内收水平边界，保留垂直边界。接口安装不等于游戏 HUD 已采用新边界；游戏保存的布局偏好也可能限制实际内收，具体设备的遮挡情况尚需实测。

Version 1.3.2 adds Original (default) and optional iOS / Custom safe areas. Custom insets range from 0 to 15% on each side, initially 5%, applied on the next tool-started mobile UI launch. They do not change the platform identity to iOS or rewrite graphics data, launch arguments, or saved game preferences. The implementation resolves the safe-area interface by name, calls its original function, and insets only the horizontal bounds. Installing the interface does not establish that the game HUD consumes those bounds; saved layout preferences may also limit the effective inset. Device-specific occlusion still requires an in-game check.

2026-10-18，用户实测确认 1.3.2 的移动端安全区功能工作正常。此确认来自用户实际运行反馈，不包含独立测量的边距数据，也不保证所有场景、分辨率或设备均无界面遮挡。

On October 18, 2026, the user confirmed that the mobile safe-area feature in 1.3.2 worked in-game. This is user-reported verification, without independently measured inset values. It does not establish that every scene, resolution, or device is free of UI occlusion.

## 界面与构建 / UI and Build

原生 WinUI 3 界面在 Windows 11、150% 显示缩放下完成检查；该组界面检查不启动或附加游戏。

The native WinUI 3 interface was checked on Windows 11 at 150% display scaling, without launching or attaching to a game.

- 已检查宽窗口双栏、窄窗口单栏滚动、底部操作栏与图标显示；其他 DPI、主题和显示器组合未逐一验证。 / Wide two-column and narrow scrollable layouts, the action bar, and icon rendering were checked. Other DPI, theme, and monitor combinations were not exhaustively checked.
- 简体中文、English、日本語、한국어 的即时切换、跟随系统、选择保存及重启恢复通过；切换过程中其余配置、FPS/FOV 数值与六个预设保持不变。 / Switching among Simplified Chinese, English, Japanese, and Korean, following the system language, saving the choice, and restoring it on restart passed. Other settings, FPS/FOV values, and the six presets remained unchanged while switching.
- 数值框、滑块、预设、重置、开关和画质来源的状态同步通过。 / Numeric inputs, sliders, presets, reset actions, switches, and graphics-source state synchronization passed.
- 日志展开、清空和按钮状态通过；复制按钮的剪贴板写入未在该组检查中执行。 / Log expansion, clearing, and button availability passed; clipboard writing was not exercised in these checks.
- 托盘最小化、重复启动恢复已有窗口、正常退出及验证前配置恢复通过。 / Tray minimization, restoring the existing window on duplicate launch, normal shutdown, and restoration of the original settings passed.
- 发布构建及源码包解包构建通过，编译 XAML 资源、图标、MiSans 原字体、字体版权通知及完整许可包含在输出中。 / Publishing and building an extracted source package passed, with compiled XAML resources, icons, the unmodified MiSans font, its copyright notice, and the complete font license present in the output.
- 此前一次字体样式调整仅通过构建与静态审查；下列 1.3.2 独立原生窗口检查补充覆盖了当前 MiSans 资源及新增控件。资源存在或构建成功本身不等于运行时字形验证。 / An earlier font-style adjustment was checked only by compilation and static review. The separate 1.3.2 native-window checks below add coverage of the current MiSans resources and new controls. Resource presence or build success alone does not establish runtime glyph rendering.

1.1.2 的数值框修复使用独立的原生 WinUI 控件窗口验证，读取本版本 FPS/FOV 控件行的 XAML，并使用相同的 WinUI 依赖与 MiSans。150% 缩放下复现了旧版微调弹层在滚动后残留；改为内嵌按钮后，两项滚动检查均无浮层，四项原生按钮调节检查通过，300/540 DIP 行宽下的数值与负小数显示检查通过。这组检查不启动正式应用或游戏，也不读取真实配置。

The 1.1.2 numeric-control fix was checked in a separate native WinUI window that reads this version's FPS/FOV row XAML and uses the same WinUI dependency and MiSans font. At 150% scaling, the old floating spin controls remained after scrolling. With inline buttons, both scroll checks had no floating overlay, all four native stepping checks passed, and numeric and negative-decimal display checks passed at row widths of 300 and 540 DIP. These checks did not launch the application or a game or read live settings.

1.2.0 的首次设置与更新弹窗使用独立原生 WinUI 窗口，直接链接本版本的弹窗代码、提示队列、语言资源、App.xaml 和 MiSans。四种语言、520/980 DIP 窗口宽度下的 32 张渲染图及 508 项原生断言通过，覆盖多客户端选择、按钮完整显示、字体、文本边界、稍后处理、重复版本抑制，以及设置、选文件和托盘状态下的更新提示延后。安装、发布说明和文件选择使用记录调用的替身；未实际下载安装、打开浏览器、修改配置或启动正式应用。

The 1.2.0 first-run and update dialogs were checked in a separate native WinUI window linking this revision's dialog code, prompt queue, localization, App.xaml, and MiSans. All 508 native assertions passed, with 32 rendered images across four languages and 520/980 DIP window widths. Checks covered client selection, complete button labels, fonts, text bounds, dismissal, same-version suppression, and deferral during setup, file picking, and tray hiding. Installer, release-note, and picker actions used recording substitutes; no real download, installation, browser action, configuration write, or application launch occurred.

1.3.0 的客户端选择行直接链接本版本的路径与扫描处理代码，并提取实际 XAML、语言资源和 MiSans，在独立原生窗口通过 **203 项检查**。四种语言、520/980 DIP 宽度下生成并检查了 24 张非空渲染图，覆盖下拉列表宽度、长路径省略、扫描状态、选择后保存、失效路径拒绝、扫描期间及运行会话期间的操作限制。文件是否存在、扫描结果和配置保存使用自编替身；不运行正式应用或系统文件选择器，不访问真实安装记录或配置。

The 1.3.0 client selector passed **203 checks** in a separate native window linking this revision's path and scan handlers and extracting the actual XAML, localization, and MiSans resources. Twenty-four nonblank renders across four languages and 520/980 DIP widths were checked for dropdown bounds, long-path ellipsis, and scan states. Behavior checks cover immediate selection persistence, unavailable-path rejection, and scan/session guards. File availability, discovery results, and settings persistence use authored substitutes; neither the production application nor the OS picker runs, and no live installation records or settings are accessed.

1.3.2 的安全区控件在独立原生 WinUI 窗口通过 **245 项断言**，四种语言、520/980 DIP 宽度下生成并检查了 **24 张非空截图**。检查链接当前实际展示与本地化代码、XAML、配置归一化及处理方法，覆盖原样与自定义切换、0/5/15%、越界与空输入、语言切换保值、移动 UI 关闭及会话忙碌/退出时的禁用状态、字体、文本边界和控件重叠。保存与会话状态使用私有替身；不运行正式应用或游戏，不将此结果视为游戏 HUD 或 iPhone 实测。

The 1.3.2 safe-area controls passed **245 assertions** in a separate native WinUI window, with **24 nonblank screenshots** checked across four languages and 520/980 DIP widths. Checks use the current presentation and localization code, XAML, configuration normalization, and handlers. They cover Original/Custom switching, 0/5/15%, out-of-range and empty input, retained values across language changes, disabled states when mobile UI is off or the session is busy/closing, fonts, text bounds, and overlap. Persistence and session state use private substitutes. Neither the production application nor a game runs; these results do not verify a game HUD or an iPhone.

## 安装与更新 / Installation and Updates

1.1.0 自包含发布及 Inno Setup 安装程序编译通过。安装包解出 **551 个应用文件**，逐文件 SHA-256 与发布目录一致，无缺失、多余或 PDB 文件；四种安装语言、编译 XAML、.NET 与 Windows App SDK 运行组件、MiSans 和许可通知均已检查。

The 1.1.0 self-contained publish and Inno Setup installer compiled successfully. All **551 application files** extracted from the installer matched the publish directory by SHA-256, with no missing, extra, or PDB files. Checks covered the four installer languages, compiled XAML, .NET and Windows App SDK runtime components, MiSans, and license notices.

1.2.1 改为依赖共享运行时的发布方式，应用目录包含 **83 个文件**，其中根目录 53 个文件；不再随应用捆绑 .NET 和 Windows App Runtime。安装程序检测已有兼容运行时，缺少时使用固定微软 HTTPS 地址下载，验证 SHA-256 后安装并重新检测，依赖就绪后才替换应用文件。旧版清理使用 468 个明确文件路径，要求相同 AppId、安装目录和应用标识，并拒绝重解析路径；不递归删除目录或卸载共享运行时。

Version 1.2.1 uses a framework-dependent publish with **83 application files**, including 53 at the root, without bundling .NET or Windows App Runtime. Setup detects compatible installed runtimes and downloads missing prerequisites from pinned Microsoft HTTPS URLs, verifies SHA-256, installs, and rechecks them before replacing application files. Legacy cleanup uses 468 explicit file paths, requires matching AppId, installation directory, and application markers, and rejects reparse paths. It does not recursively delete directories or uninstall shared runtimes.

更新检查、下载、长度与 SHA-256 校验、取消、失败清理和交接命令边界使用自编 HTTP 响应与临时文件验证。安装保护使用本测试自建的随机全局互斥体，验证可见性、多个持有者及最后关闭后的释放；未使用真实应用的互斥体。

Update checks, downloads, length and SHA-256 verification, cancellation, failure cleanup, and handoff command boundaries used authored HTTP responses and temporary files. Installer-guard checks used an owned, randomly named global mutex to verify visibility, multiple holders, and release after the final close; they did not use the application's actual mutex.

1.1.1 新增 50 项检查，覆盖明确限流时的同仓库发布清单后备、限流期限记忆、可信 HTTPS 跳转、取消、超时、异常清单拒绝及签名下载地址的日志脱敏。另以真实匿名请求复现 API 返回 403、额度为零，而同一网络的正式发布资产可返回 200；这些联网观察与不联网的合成测试分开记录。

Version 1.1.1 adds 50 checks for same-repository release-manifest fallback on explicit rate limits, remembered retry deadlines, trusted HTTPS redirects, cancellation, timeouts, rejection of invalid manifests, and withholding signed download URLs from logs. Separate live anonymous requests reproduced an API 403 with no remaining quota while a stable release asset returned 200 on the same network. These network observations are distinct from the offline synthetic suite.

本轮未运行安装包或更新助手，未触发 UAC，也未访问真实配置或启动游戏。生产环境的受保护下载目录、助手就绪握手、安装目录沿用、跨用户会话升级保护、卸载和升级后重启尚未进行完整运行验证。新增更新区、退出开关及调度器倒计时通过编译和代码审查，未完成原生界面操作复查。

This run did not execute the installer or update helper, trigger UAC, read live settings, or launch a game. The production protected download directory, helper readiness handshake, reuse of an existing installation directory, upgrade protection across user sessions, uninstallation, and restart after upgrading have not been exercised end to end. The new update controls, exit switch, and dispatcher countdown passed compilation and code review, without a native UI interaction recheck.

## 合成测试 / Synthetic Tests

2026-10-18 的 1.2.0 测试运行通过 **540 项检查**。具体数量以对应源码版本的运行输出为准。

The October 18, 2026 run for 1.2.0 passed **540 checks**. The output for the corresponding source revision is authoritative.

1.3.0 的便携测试通过 **633 项检查**，其中 39 项新增客户端列表检查覆盖本地路径规范化、重复与失效路径、扫描合并、手动路径保留、枚举上限、旧配置兼容、独立克隆与 JSON 往返。文件是否存在由自编数据提供，不读取真实客户端或配置。

The portable suite for 1.3.0 passed **633 checks**, including 39 new client-list checks for local path normalization, duplicate and unavailable paths, scan merging, remembered manual paths, enumeration bounds, legacy settings, independent clones, and JSON roundtrips. File availability uses authored data rather than live clients or settings.

1.3.1 的便携测试通过 **634 项检查**。新增检查将生产平台 getter 写入自有只读可执行内存并实际调用，确认返回 WindowsEditor (7)，而非 Android (11)；该检查不启动或读取游戏。

The portable suite for 1.3.1 passed **634 checks**. The added check writes the production platform getter into owned read-execute memory, calls it, and verifies WindowsEditor (7) rather than Android (11), without launching or reading a game.

1.3.2 的便携测试通过 **857 项检查**。新增检查实际执行生产安全区函数，覆盖不同分辨率、已有水平边界、0/15% 边界、非有限或无效矩形的原样保留、调用约定、原函数只调用一次及操作系统栈展开。自编 PE 覆盖按名称定位、缺失与歧义拒绝、原样/0% 不解析可选接口；自有内存覆盖 RX 发布、真实展开表先注册、指针 CAS 竞争拒绝与数据页保护还原。另验证旧配置兼容、四语资源和启动配置快照不影响实时 FPS 配置。

The portable suite for 1.3.2 passed **857 checks**. Added checks execute the production safe-area function across resolutions, existing horizontal bounds, 0/15% limits, bit-preserving fallback for nonfinite or invalid rectangles, calling conventions, exactly one original call, and OS stack unwinding. Authored PE files cover named resolution, missing/ambiguous rejection, and skipping the optional interface in Original/0% modes. Owned-memory checks cover RX publication, real unwind registration before publication, CAS conflict rejection, and restoration of data-page protection. Further checks cover legacy settings, four-language resources, and launch snapshots that leave live FPS configuration intact.

另新增 45 项映射查询检查和 9 项挂起启动检查。旧实现在自建挂起测试子进程中复现了系统模块不匹配错误；修复后，在不恢复其主线程的情况下，系统导出定位、真实展开表注册、引导线程就绪及取消均通过，随后仅恢复该测试子进程运行 `--help` 并正常退出。检查区分内存映射覆盖范围与 PE 镜像尺寸，保留系统文件身份、PE 头、可执行页和入口字节校验；这不等同于真实客户端中的移动 UI 启动验证。

There are also 45 new mapped-image checks and nine suspended-start checks. The old implementation reproduced the system-module mismatch in an owned suspended test child. After the repair, system-export resolution, actual unwind registration, and bootstrap readiness and cancellation passed without resuming its primary thread; only that test child was then resumed into `--help` and exited normally. The checks distinguish mapping coverage from PE image size and retain system-file identity, PE-header, executable-page, and entry-byte verification. They do not establish mobile UI launch behavior in a real client.

`tests/HSRUnlocker.Tests` 使用合成 PE、固定测试数据、临时目录和自有内存，不读取已安装游戏、真实设置或注册表，不联网，也不启动游戏。原生调用与展开测试会执行本项目自行编写的合成机器码，并使用两个有界的同程序集测试子进程。

`tests/HSRUnlocker.Tests` uses synthetic PE files, authored fixtures, temporary directories, and owned memory. It does not read installed games, live settings, or the registry, access the network, or launch a game. Native calling-convention and unwind checks execute synthetic machine code authored for this project and use two bounded children running the same test assembly.

覆盖范围包括结构解析与扫描、动态定位、无效或歧义结构拒绝、调用约定、并发与还原、移动平台参数、配置归一化和权限检查；语言测试覆盖系统语言选择、手动选择与配置兼容、四语资源及格式参数、数字格式和并发切换。合成测试通过不替代真实环境中的功能观察。

Coverage includes parsing and scanning, dynamic resolution, rejection of invalid or ambiguous structures, calling conventions, concurrency and restoration, mobile-platform arguments, settings normalization, and privilege checks. Localization checks cover system and manual selection, settings compatibility, four-language catalogs and format values, numeric formatting, and concurrent language changes. Synthetic tests do not replace observing behavior in the intended environment.

1.1.0 另覆盖旧配置默认值、新开关的保存与克隆、正式版本比较、固定仓库与安装包选择、响应大小和超时边界、下载进度、损坏或截断数据拒绝、取消与占用文件清理，以及安装保护的原生句柄生命周期。

Additional 1.1.0 coverage includes legacy preference defaults, persistence and cloning of the new switches, stable version ordering, fixed repository and installer selection, response size and timeout bounds, download progress, rejection of corrupt or truncated data, cancellation and locked-file cleanup, and native handle lifetime for the installer guard.

1.2.0 新增 20 项安装路径检测和 12 项更新提示队列检查。安装检测使用自编注册表与文件读取数据，覆盖启动器记录、旧安装信息、自定义与多语言目录、重复和失效路径、访问失败及读取上限，不读取真实安装记录。首次启动的完整实机发现流程未在正式应用中运行。

Version 1.2.0 adds 20 installation-discovery and 12 update-prompt queue checks. Discovery uses authored registry and file-read data for launcher records, legacy installations, custom multilingual directories, duplicate and stale paths, access failures, and read limits, without reading live installation records. Complete first-run discovery was not executed in the production application.

`tests/Packaging.Tests.ps1` 的 **20 项打包检查**使用临时自编 NuGet 包，覆盖许可与通知原样复制、框架下载依赖、中文路径、隐私清单和越界路径拒绝，不读取真实游戏或配置。

The **20 packaging checks** in `tests/Packaging.Tests.ps1` use temporary authored NuGet packages. They cover verbatim license and notice copying, framework download dependencies, Unicode paths, manifest privacy, and rejection of unsafe paths, without reading a game or live settings.

1.2.1 的 **39 项安装元数据检查**覆盖微软地址与哈希、SDK 版本匹配、依赖式发布、x64 引导 DLL、旧版文件清单及越界路径拒绝。另通过 **33 项运行时流程**和 **80 项升级清理**原生 Pascal 检查，直接包含安装器中的实际策略代码，使用自编的检测、下载、执行、注册表和文件属性替身。覆盖取消、校验失败、进程失败、安装后重检、重启及重试保护、安装归属和路径保护；不执行生产安装包或微软运行时安装器，不读取真实安装注册表。

Version 1.2.1 adds **39 installer metadata checks** for Microsoft URLs and hashes, SDK identity, framework-dependent publishing, x64 bootstrap DLLs, the legacy inventory, and unsafe paths. **33 runtime-workflow** and **80 upgrade-cleanup** native Pascal checks include the installer's actual policy code with authored detection, download, execution, registry, and file-attribute substitutes. Coverage includes cancellation, integrity failures, process failures, post-install detection, restart and retry gates, ownership, and path protection. These checks do not execute the production installer or Microsoft runtime installers or read live installation registry data.

## 本地复现 / Local Checks

```powershell
dotnet run --project .\tests\HSRUnlocker.Tests\HSRUnlocker.Tests.csproj -c Release
powershell -NoProfile -File .\tests\Packaging.Tests.ps1
powershell -NoProfile -File .\tests\InstallerMetadata.Tests.ps1
powershell -NoProfile -File .\build.ps1 -OutDir .\artifacts\win-x64
# Inno Setup 7.1+
powershell -NoProfile -File .\tests\Installer.Tests.ps1
powershell -NoProfile -File .\tests\LegacyCleanup.Tests.ps1
powershell -NoProfile -File .\build-installer.ps1 -OutDir .\artifacts\installer
```

发布目录必须为空或不存在；构建脚本不启动应用或游戏。只向仓库提交有权公开的源码、合成测试和脱敏文字说明，不提交游戏文件、资源、截图、转储、原始日志或私人配置。

Publish into an empty or nonexistent directory; the build script does not launch the application or game. Submit only source, synthetic tests, and sanitized text that you have the right to publish. Do not submit game files, assets, screenshots, dumps, raw logs, or private settings.
