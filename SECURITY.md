# 安全说明 / Security

[简体中文](#简体中文) | [English](#english)

## 简体中文

### 权限与使用风险

本工具需要管理员权限，会读写目标进程内存，并会在用户选择启动时启动客户端。请先阅读源码与使用说明，仅运行自行构建或来自可信来源的程序。进程内存修改可能带来稳定性与账号风险，本项目不保证使用结果。

应用配置与运行日志保存在 `%LOCALAPPDATA%\HSRUnlocker\`。报告问题不需要上传该目录或完整日志。

### 报告范围

请报告工具本身的问题，例如：

- 向非目标进程写入，或意外修改游戏目录中的文件。
- 高权限路径可被低权限进程利用。
- 依赖项被替换，或构建产物与相应源码不一致。
- 意外读取、保存或公开敏感信息。

### 如何报告

**公开 Issue 不是私密渠道。** 如果仓库的 Security 页面提供私密漏洞报告入口，请优先通过该入口联系维护者；入口不可用时，可先在 Issue 中仅询问私密联系方式，不要公开漏洞利用细节或敏感材料。

初次报告只需提供最小脱敏信息：

- 工具版本或源码提交、Windows 版本，以及是否从源码构建。
- 预期行为与实际行为。
- 不含账号、路径或其他敏感数据的最少复现步骤；可以使用合成用例。
- 必要时手工摘录并脱敏的错误信息，不附完整日志。

不要上传游戏文件、资源、截图、转储、解包产物、非公开资料、个人配置、凭据或可直接利用的高权限攻击材料。维护者如需更多信息，应先商定安全的沟通方式与必要范围。

## English

### Privileges And Risks

This tool requires administrator privileges, reads and writes target-process memory, and launches a game client when requested. Review the source and usage instructions first. Run only software you build yourself or obtain from a trusted source. Process-memory modifications may affect stability and put accounts at risk; this project does not guarantee an outcome.

Application settings and runtime logs are stored in `%LOCALAPPDATA%\HSRUnlocker\`. You do not need to upload that directory or complete logs to report an issue.

### What To Report

Report problems in the tool itself, such as:

- Writes to an unintended process, or unintended changes to files in a game directory.
- Privileged operations that a lower-privilege process can exploit.
- Replaced dependencies, or build artifacts that do not match their corresponding source.
- Unintended reading, storage, or disclosure of sensitive information.

### How To Report

**Public issues are not a private channel.** If the repository's Security page offers private vulnerability reporting, use it to contact the maintainer. Otherwise, ask only for a private contact method in an issue. Do not publish exploit details or sensitive material.

An initial report needs only minimal, sanitized information:

- The tool version or source commit, Windows version, and whether you built from source.
- Expected and actual behavior.
- The fewest reproduction steps necessary, without account details, paths, or other sensitive data. Synthetic examples are welcome.
- Manually selected and sanitized error text when necessary, not complete logs.

Do not upload game files, assets, screenshots, dumps, unpacked output, nonpublic material, personal settings, credentials, or directly usable material for exploiting elevated privileges. If more information is needed, first agree on a safe communication method and the necessary scope with the maintainer.
