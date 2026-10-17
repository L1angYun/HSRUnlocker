# HSRUnlocker

<p align="center">
  <img src="gui/Assets/AppIcon.png" width="96" height="96" alt="HSRUnlocker" />
</p>

崩坏：星穹铁道帧率解锁工具，支持 FOV 调整、移动端 UI 和相机反虚化。使用 C# / .NET 8 与原生 WinUI 3 界面。

A Honkai: Star Rail FPS unlocker with FOV adjustment, mobile UI, and camera anti-fade. Built with C# / .NET 8 and WinUI 3.

[简体中文](#简体中文) | [English](#english)

## 简体中文

### 功能

| 功能 | 说明 |
|---|---|
| 帧率解锁 | 设置 10–480 FPS 上限，默认 120；游戏重设上限时自动恢复目标值。实际帧率取决于硬件与游戏负载。 |
| FOV 调整 | 视野增量范围为 −60° 至 +60°，支持滑块、数值输入和一键归零。 |
| 移动端 UI | 启动游戏时启用触控界面，可沿用客户端已保存的画质设置。 |
| 相机反虚化（实验性） | 关闭相机靠近或俯仰视角引起的角色淡出，支持运行时开关。 |
| 启动与附加 | 从工具启动游戏，或附加到已运行的游戏进程。 |
| 托盘与配置 | 最小化到托盘、单实例、自动保存设置，可选择随工具启动游戏。 |

程序使用动态寻址，游戏更新后仍可能需要适配。已验证的客户端与功能范围见[验证记录](docs/VALIDATION.md)。

### 运行要求

- Windows 11 22H2（22621）或更高版本，x64。
- .NET 8 x64 运行时与 Windows App Runtime（对应 Windows App SDK 2.5）。
- 管理员权限。

### 使用方法

1. 运行构建输出目录中的 `HSRUnlocker.exe`，保留同目录的依赖文件。
2. 选择游戏客户端的 `StarRail.exe`。
3. 设置目标帧率、FOV 和所需开关。
4. 点击「启动游戏」，或用「附加游戏」连接已运行的客户端。
5. 点击「停止并还原」恢复帧率、FOV 与反虚化修改。

使用移动端 UI 时，请先退出正在运行的游戏，再开启选项并通过工具启动。若提示缺少画质设置，请先以普通模式运行一次对应客户端。「画质设置来源」默认自动识别；国服和国际服选项只决定读取哪份已保存的画质设置。

FOV 在下一次镜头更新时生效。最小化后工具会进入托盘，双击托盘图标可恢复窗口。

配置和日志保存在 `%LOCALAPPDATA%\HSRUnlocker\`。

### 从源码构建

安装 .NET 8 SDK，在项目根目录执行：

```powershell
dotnet run --project .\tests\HSRUnlocker.Tests\HSRUnlocker.Tests.csproj -c Release
powershell -NoProfile -File .\build.ps1 -OutDir .\artifacts\win-x64
```

输出位于 `artifacts\win-x64`。输出目录必须为空；再次构建时请指定新的空目录。更多开发约定见[参与开发](CONTRIBUTING.md)。

### 关于 Vibe Coding

本项目采用 **Vibe Coding** 方式开发：AI 参与功能拆解、界面设计、代码、测试和文档编写。这不是一个全部手写的项目，也不会将 AI 生成内容包装成经过独立专业审计的成果。

对这种开发方式的担忧有其理由。AI 可能遗漏边界条件、引入安全问题，生成的测试也可能重复实现中的错误假设。本工具需要管理员权限并修改进程内存，不能用“AI 写的”推卸责任，也不能把“测试通过”当作安全保证。

请根据具体实现和可复现的行为评价这个项目，而不是把开发方式当作质量背书。源码和验证范围公开，欢迎针对正确性、权限使用、稳定性或测试覆盖提出问题和 Pull Request。是否接受这种开发方式、是否运行本工具，由你根据自己的审查与风险判断决定。

### 免责声明

本项目为非官方工具，与游戏开发商及发行商无关联。修改游戏进程可能违反游戏服务条款并带来账号风险，使用者需自行承担相关后果。本软件不提供任何担保。

## English

### Features

| Feature | Description |
|---|---|
| FPS unlock | Set a 10–480 FPS limit, with a default of 120. Automatically reapplies the target when the game resets it. Actual FPS depends on hardware and game load. |
| FOV adjustment | Apply an offset from −60° to +60° using the slider or numeric input, with a reset button. |
| Mobile UI | Enable the touch interface when launching the game and reuse saved graphics settings. |
| Camera anti-fade (experimental) | Disable character fading caused by camera proximity or elevation; can be toggled while playing. |
| Launch and attach | Launch the game through the tool or attach to an existing game process. |
| Tray and settings | Minimize to tray, single-instance behavior, automatic settings persistence, and optional game launch when the tool opens. |

Addresses are resolved dynamically, but game updates may still require compatibility changes. See the [validation record](docs/VALIDATION.md) for tested clients and feature coverage.

### Requirements

- Windows 11 22H2 (build 22621) or later, x64.
- .NET 8 x64 Runtime and Windows App Runtime for Windows App SDK 2.5.
- Administrator privileges.

### Usage

1. Run `HSRUnlocker.exe` from the build output directory, keeping its dependency files together.
2. Select the game client's `StarRail.exe`.
3. Set the target FPS, FOV offset, and desired options.
4. Click 「启动游戏」 (Launch), or 「附加游戏」 (Attach) for a running client.
5. Click 「停止并还原」 (Stop and Restore) to restore FPS, FOV, and anti-fade changes.

To use mobile UI, close any running game, enable the option, then launch through the tool. If graphics settings are missing, run that client normally once first. The graphics-settings source is detected automatically by default; the China/global choices only select which saved settings to read.

FOV changes apply on the next camera update. Minimizing moves the tool to the tray; double-click its tray icon to restore the window.

Settings and logs are stored in `%LOCALAPPDATA%\HSRUnlocker\`.

### Build From Source

Install the .NET 8 SDK, then run from the repository root:

```powershell
dotnet run --project .\tests\HSRUnlocker.Tests\HSRUnlocker.Tests.csproj -c Release
powershell -NoProfile -File .\build.ps1 -OutDir .\artifacts\win-x64
```

The output is in `artifacts\win-x64`. The output directory must be empty; choose a new empty directory for subsequent builds. See [CONTRIBUTING.md](CONTRIBUTING.md) for development guidelines.

### About Vibe Coding

This project uses **Vibe Coding**: AI contributes to feature planning, UI design, code, tests, and documentation. It is not an entirely handwritten project, and AI-generated work is not presented as an independent professional audit.

Concerns about this approach are reasonable. AI can miss edge cases, introduce security problems, and generate tests that repeat the implementation's incorrect assumptions. This tool runs with administrator privileges and modifies process memory. "AI wrote it" does not excuse defects, and passing tests does not establish safety.

Judge the project by its implementation and reproducible behavior, not by treating its development method as a quality endorsement. The source and validation scope are public. Reports and pull requests addressing correctness, privileges, stability, or test coverage are welcome. Whether to accept this development approach or run the tool is your decision, based on your own review and assessment of the risks.

### Disclaimer

This is an unofficial tool, unaffiliated with the game's developer or publisher. Modifying the game process may violate its terms of service and put accounts at risk. Use it at your own risk. The software is provided without warranty.

## License

[GPL-3.0-only](LICENSE). Third-party dependencies retain their respective licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
