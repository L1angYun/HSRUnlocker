# 参与开发 / Contributing

[简体中文](#简体中文) | [English](#english)

## 简体中文

### 构建与测试

```powershell
dotnet build .\gui\HSRUnlocker.csproj -c Release -r win-x64
dotnet run   --project .\tests\HSRUnlocker.Tests\HSRUnlocker.Tests.csproj -c Release
powershell -NoProfile -File .\tests\Packaging.Tests.ps1
```

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
```

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
