# 拆包智录代码签名政策

## 适用范围

本政策适用于“拆包智录”的 Windows 桌面端、工位主机、兼容同步服务和 Velopack 安装器。安卓 APK 使用项目固定的 Release 密钥签名，密钥不提交源码仓库。

## 构建与签名来源

- 正式产物必须由公开仓库 `907609732/unpack-vision` 的 GitHub Actions 从受保护的 `main` 分支和版本标签构建。
- Windows 计划使用 SignPath.io 提供的自动化签名服务和 SignPath Foundation 证书；在服务批准并完成流水线接入前，所有 Windows 包均保持 Prerelease。
- 签名请求必须关联构建提交、GitHub工作流和不可变构建产物，不接受个人电脑直接上传的二进制作为稳定版。
- 第三方上游二进制不冒用本项目证书；发布包保留其许可证和原始签名状态。

## SignPath 接入状态

- 已于 2026-08-08 向 SignPath Foundation 提交开源项目申请，目前等待审核和项目配置。
- 审核完成前不得创建正式稳定版标签，也不得把 `WINDOWS_STABLE_SIGNING_READY` 设置为 `true`。
- 流水线已预留 SignPath 官方 GitHub Action，并固定到经过验证的完整提交 SHA；审核通过后只需配置 SignPath 返回的组织、项目、策略和制品配置参数。
- GitHub Actions 使用两阶段签名：先签署桌面端、工位主机和兼容服务，再由 Velopack 打包；随后签署外层安装器。这样安装后的程序和下载到的安装器都具有可信 Authenticode 签名。

启用签名前需要配置：

```text
Repository secret
- SIGNPATH_API_TOKEN

Repository variables
- SIGNPATH_ORGANIZATION_ID
- SIGNPATH_PROJECT_SLUG
- SIGNPATH_SIGNING_POLICY_SLUG
- SIGNPATH_APPLICATION_ARTIFACT_CONFIGURATION_SLUG
- SIGNPATH_INSTALLER_ARTIFACT_CONFIGURATION_SLUG
- WINDOWS_STABLE_SIGNING_READY=true  # 最后一步才设置
```

应用制品配置只允许签署本项目生成的 `UnpackVision.*.dll`、三个主程序 EXE 及其同名 DLL；安装器制品配置只允许签署 `*Setup.exe`。MediaMTX、.NET 运行时、Velopack/Squirrel 和其他第三方文件必须排除。

## 项目角色

- 提交者、维护者与代码审查者：五成（GitHub：`907609732`）。
- 发布和签名批准者：五成（GitHub：`907609732`）。
- 外部贡献必须通过Pull Request，并通过主分支必需检查和会话解决要求。

## 稳定版门禁

- GitHub仓库变量 `WINDOWS_STABLE_SIGNING_READY` 默认不存在或为 `false`，此时Release只能标记为Prerelease。
- 只有完成可信签名配置后才能设置为 `true`；工作流仍会逐个验证本项目Windows可执行文件和安装器的Authenticode状态。
- 任一自有文件缺失或签名状态不是 `Valid` 时，稳定版发布立即失败。
- SignPath 参数缺失、签名请求被拒绝、审批超时或签名产物未返回时，工作流立即失败，不回退为伪稳定版。
- 正式发布同时提供SHA256、构建来源证明、发行说明和第三方许可证。

## 隐私与安全

软件默认本地处理快递单号、录像、Excel、备注和配对数据。除用户明确启用的GitHub更新检查和匿名日活外，不向互联网发送业务数据。完整说明见 [隐私政策](PRIVACY.md) 和 [安全政策](../SECURITY.md)。

免费代码签名计划由 SignPath.io 提供，证书由 SignPath Foundation 提供。每个正式签名请求需要发布批准者确认。
