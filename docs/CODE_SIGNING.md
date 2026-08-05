# 拆包智录代码签名政策

## 适用范围

本政策适用于“拆包智录”的 Windows 桌面端、工位主机、兼容同步服务和 Velopack 安装器。安卓 APK 使用项目固定的 Release 密钥签名，密钥不提交源码仓库。

## 构建与签名来源

- 正式产物必须由公开仓库 `907609732/unpack-vision` 的 GitHub Actions 从受保护的 `main` 分支和版本标签构建。
- Windows 计划使用 SignPath.io 提供的自动化签名服务和 SignPath Foundation 证书；在服务批准并完成流水线接入前，所有 Windows 包均保持 Prerelease。
- 签名请求必须关联构建提交、GitHub工作流和不可变构建产物，不接受个人电脑直接上传的二进制作为稳定版。
- 第三方上游二进制不冒用本项目证书；发布包保留其许可证和原始签名状态。

## 项目角色

- 提交者、维护者与代码审查者：五成（GitHub：`907609732`）。
- 发布和签名批准者：五成（GitHub：`907609732`）。
- 外部贡献必须通过Pull Request，并通过主分支必需检查和会话解决要求。

## 稳定版门禁

- GitHub仓库变量 `WINDOWS_STABLE_SIGNING_READY` 默认不存在或为 `false`，此时Release只能标记为Prerelease。
- 只有完成可信签名配置后才能设置为 `true`；工作流仍会逐个验证本项目Windows可执行文件和安装器的Authenticode状态。
- 任一自有文件缺失或签名状态不是 `Valid` 时，稳定版发布立即失败。
- 正式发布同时提供SHA256、构建来源证明、发行说明和第三方许可证。

## 隐私与安全

软件默认本地处理快递单号、录像、Excel、备注和配对数据。除用户明确启用的GitHub更新检查和匿名日活外，不向互联网发送业务数据。完整说明见 [隐私政策](PRIVACY.md) 和 [安全政策](../SECURITY.md)。

免费代码签名计划由 SignPath.io 提供，证书由 SignPath Foundation 提供。每个正式签名请求需要发布批准者确认。
