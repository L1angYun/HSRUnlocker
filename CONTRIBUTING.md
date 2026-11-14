# 参与开发 / Contributing

[简体中文](#简体中文) | [English](#english)

## 发布命令 / Release commands

在仓库根目录运行。构建应用需要 .NET 8 SDK，编译安装包还需要 Inno Setup 7.1 或更高版本。

Run from the repository root. Install the .NET 8 SDK for the app build and Inno Setup 7.1 or later for the installer.

```powershell
powershell -NoProfile -File .\build.ps1 -OutDir .\artifacts\win-x64
powershell -NoProfile -File .\build-installer.ps1 -OutDir .\artifacts\installer
```

安装包构建后，用 PowerShell 7.2 或更高版本准备发布附件。

After building the installer, prepare the release assets in PowerShell 7.2 or later.

```powershell
$version = (Get-Content .\artifacts\installer\update.json -Raw | ConvertFrom-Json).tag_name.Substring(1)
$source = .\package-source.ps1 -OutDir .\artifacts\source | ConvertFrom-Json
$release = @{
    Version = $version
    InstallerPath = ".\artifacts\installer\HSRUnlocker-$version-win-x64-Setup.exe"
    SourceArchivePath = $source.Archive
    OutDir = '.\artifacts\release-assets'
}
.\scripts\Prepare-ReleaseAssets.ps1 @release
```

校验下载时，将安装包和同一版本的 `SHA256SUMS.txt` 放在同一目录。在 PowerShell 中将下方示例的 `<version>` 换成实际版本号，核对两处 SHA-256 值是否一致。

To verify a download, place the installer and `SHA256SUMS.txt` from the same release in one folder. Open PowerShell there, replace `<version>` in the example with the actual version, and compare the two SHA-256 values.

```powershell
Get-FileHash '.\HSRUnlocker-<version>-win-x64-Setup.exe' -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

## 简体中文

### 构建与测试

```powershell
dotnet build .\gui\HSRUnlocker.csproj -c Release -r win-x64
dotnet run   --project .\tests\HSRUnlocker.Tests\HSRUnlocker.Tests.csproj -c Release
powershell -NoProfile -File .\tests\Packaging.Tests.ps1
powershell -NoProfile -File .\tests\InstallerMetadata.Tests.ps1
pwsh -NoProfile -File .\tests\ReleaseAssets.Tests.ps1
```

发布附件测试需要 PowerShell 7.2 或更高版本，只使用临时目录中的合成文件。

原生安装程序测试需要 Inno Setup 7.1 或更高版本，并显式提供实际 `ISCC.exe` 路径；以下入口不会因缺少编译器而跳过测试：

```powershell
powershell -NoProfile -File .\tests\NativeFixtures.Tests.ps1 -CompilerPath '<full-path-to-ISCC.exe>'
```

该入口编译并运行依赖检查与旧版清理两组合成测试，并核验 `RedStub` 负向对照按预期断言失败。可用 `-EvidenceDir` 指定新的空证据目录，默认使用临时目录。CI 配置使用同一入口，并下载及校验固定版本 Inno Setup 7.1.0。测试不执行应用或运行时安装包，也不修改真实安装或注册表。

### 打包与发布

见[发布命令](#发布命令--release-commands)。应用和安装程序的构建目录必须为空；应用默认使用共享的 .NET 8 x64 和 Windows App Runtime，可为 `build.ps1` 添加 `-SelfContained` 随包附带运行时。安装包使用共享运行时；可为 `build-installer.ps1` 添加 `-CompilerPath` 以指定 `ISCC.exe`。固定的运行时下载地址和校验值在 [installer/runtime-dependencies.json](installer/runtime-dependencies.json)。

安装程序构建会根据成品生成 `update.json`。发布附件脚本验证清单和安装包、检查源码 ZIP，生成 `SHA256SUMS.txt` 并核对输出文件。输出目录需为空且与输入目录分开；脚本不上传文件。

GitHub 和 Gitee 的同一版本使用相同附件。先上传并核对安装包、源码 ZIP 和 `SHA256SUMS.txt`，最后上传 `update.json`。同步仓库源码不会同步 Gitee 的发布附件。

### 代码约定

- 生产代码不得依赖固定目标函数地址、固定静态量地址或版本特定 RVA。目标应由结构与运行时校验动态确定。
- 无法唯一确定目标时应拒绝操作并给出明确错误，不猜测，也不按安装目录名称判断兼容性。
- 功能变化应同步更新 [README.md](README.md)，如实区分构建结果、合成测试和实际功能观察。
- 默认测试应能在未安装游戏的机器上运行，只使用自编合成数据、自有内存和临时目录，不读取真实设置或注册表，不联网、不启动游戏。
- 用户可见文案使用 `L10n.T`，资源位于 `gui/Localization/*.json`。新增文案同时提供 `zh-CN`、`en-US`、`ja-JP`、`ko-KR`，保留各语言一致的格式参数；路径、协议标识和模块名不翻译。

### 提交内容

只提交你本人有权公开并授权的代码、合成测试和脱敏文字说明。问题描述与复现步骤同样适用以下限制：

- 不提交游戏客户端、游戏资源或游戏截图，也不提交解包、反编译产物或内存转储。
- 不提交第三方程序、其二进制载荷或未经许可的素材。
- 不提交非公开资料、原始日志、本机配置、个人路径、账号信息、凭据或其他隐私数据。
- 不要求他人上传客户端、转储、解包产物或非公开资料。优先使用最小合成用例和不含敏感内容的文字步骤。

提交前请检查差异及新增文件；`.gitignore` 不能代替人工检查。涉及权限边界或敏感信息的问题请先阅读 [SECURITY.md](SECURITY.md)。

### 许可

提交即表示你同意以 GPL-3.0-only 授权你的贡献，并确认你有权这样做。不得以本项目的许可替代第三方内容自身的许可或授权。

## English

### Build And Test

```powershell
dotnet build .\gui\HSRUnlocker.csproj -c Release -r win-x64
dotnet run   --project .\tests\HSRUnlocker.Tests\HSRUnlocker.Tests.csproj -c Release
powershell -NoProfile -File .\tests\Packaging.Tests.ps1
powershell -NoProfile -File .\tests\InstallerMetadata.Tests.ps1
pwsh -NoProfile -File .\tests\ReleaseAssets.Tests.ps1
```

Release asset tests require PowerShell 7.2 or later and use only synthetic files in temporary directories.

Native installer tests require Inno Setup 7.1 or later and an explicit path to the actual `ISCC.exe`. This entrypoint does not skip tests when the compiler is missing:

```powershell
powershell -NoProfile -File .\tests\NativeFixtures.Tests.ps1 -CompilerPath '<full-path-to-ISCC.exe>'
```

It compiles and runs both prerequisite and legacy-cleanup synthetic suites, and verifies that the `RedStub` negative control fails its intended assertions. Use `-EvidenceDir` to select a new empty evidence directory; the default is a temporary directory. CI is configured to use the same entrypoint and download and verify pinned Inno Setup 7.1.0. Tests do not execute application or runtime installers or modify real installations or the registry.

### Packaging and releases

See the [release commands](#发布命令--release-commands). Use empty output directories for the app and installer builds. The app uses shared .NET 8 x64 and Windows App Runtime installations by default; add `-SelfContained` to `build.ps1` to bundle them. The installer uses shared runtimes. Pass `-CompilerPath` to `build-installer.ps1` to select `ISCC.exe`. Pinned runtime downloads and hashes are in [installer/runtime-dependencies.json](installer/runtime-dependencies.json).

The installer build uses the finished installer to generate `update.json`. The release asset script checks the manifest and installer, verifies that it can read the source ZIP, generates `SHA256SUMS.txt`, and rechecks the output files. Use an empty output directory separate from the inputs. The script prepares files locally.

Use identical assets for the same release on GitHub and Gitee. Upload and check the installer, source ZIP, and `SHA256SUMS.txt` first, then upload `update.json` last. Synchronizing the repository does not publish release assets on Gitee.

### Code Conventions

- Production code must not depend on fixed target-function addresses, static-data addresses, or version-specific RVAs. Resolve targets through structural analysis and runtime validation.
- Refuse an operation with a clear error when a target cannot be identified uniquely. Do not guess or infer compatibility from installation-directory names.
- Update [README.md](README.md) when behavior changes. Distinguish build results, synthetic tests, and observed behavior in the intended environment.
- Default tests must run without an installed game, using only authored synthetic data, owned memory, and temporary directories. They must not read live settings or the registry, access the network, or launch a game.
- Use `L10n.T` for user-visible messages, with resources in `gui/Localization/*.json`. Add `zh-CN`, `en-US`, `ja-JP`, and `ko-KR` translations with matching format arguments. Do not translate paths, protocol identifiers, or module names.

### Submission Content

Submit only code, synthetic tests, and sanitized text that you have the right to publish and license. The following restrictions also apply to issue descriptions and reproduction steps:

- Do not submit game clients, game assets, game screenshots, unpacked or decompiled output, or memory dumps.
- Do not submit third-party programs, their binary payloads, or materials without the necessary permission.
- Do not submit nonpublic material, raw logs, local settings, personal paths, account information, credentials, or other private data.
- Do not ask others to upload clients, dumps, unpacked output, or nonpublic material. Prefer minimal synthetic examples and text instructions without sensitive information.

Review the diff and newly added files before submitting. `.gitignore` is not a substitute for inspection. Read [SECURITY.md](SECURITY.md) first for issues involving privilege boundaries or sensitive information.

### License

By submitting a contribution, you agree to license it under GPL-3.0-only and confirm that you have the right to do so. The project's license does not replace a third party's own license or permission.
