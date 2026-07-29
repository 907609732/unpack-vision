# 2.2.0 安全审计记录

审计日期：2026-07-29

## 已完成

- 局域网控制从明文 HTTP 迁移到自签名证书 HTTPS，安卓端固定证书 SHA256 指纹。
- 手机推流地址改为 RTSPS；MediaMTX 的发布与读取继续按设备权限鉴权。
- 安全迁移时废止旧明文配对设备，保留历史记录并要求重新配对。
- 管理、设备列表、配对会话和本机文件导入接口只允许回环地址。
- 健康接口不再返回当前单号、录像状态、路径或设备详情。
- 添加请求体上限、字段长度、事件幂等、配对失败锁定和按来源速率限制。
- 录像及缩略图读取必须位于配置的录像根目录，使用框架 Range/ETag 实现。
- Host 仅接受当前工位绑定的局域网地址；配对地址不再信任请求 Host。
- 安卓 Release 禁止局域网明文；Debug 明文仅作为 ADB 回环兜底。
- 更新前不启用统计；匿名统计实现为空操作。
- GitHub Actions 依赖固定到完整提交 SHA，并加入 CodeQL、Gitleaks、OSV、Trivy 与 actionlint。

## 本机扫描结果

- `dotnet list package --vulnerable --include-transitive`：所有 6 个项目均未发现已知漏洞。
- OSV Scanner 2.4.0：未发现问题。
- Trivy 0.72.0：高危/严重漏洞、密钥、配置与许可证扫描退出码为 0。
- Gitleaks 8.30.1：5 个 Git 提交未发现密钥；工作目录的初次 5 项命中全部位于已忽略的 Android 编译缓存，不属于源码或提交内容。
- .NET Release：65 项测试通过，包括 RSA 私钥签名、TLS 握手和证书重载稳定性。
- Android Debug：10 项单元测试与 APK 构建通过；Release 同时通过 Lint、R8 压缩和签名验证。
- StationHost 隔离数据目录运行检查：健康状态为 `healthy`、TLS 开启，只监听回环 `5271` 和当前 Windows 专用网络地址的 `5273`，配对地址为 HTTPS；错误 Host 头返回 400，未监听公共网络或 `0.0.0.0`。
- MediaMTX 1.18.2 冒烟测试：设备配对、权限鉴权、RTSPS 发布地址、HTTPS/WHEP 地址和运行时 TLS 配置均成功生成；选用未被 Windows 保留的 RTSPS 端口 `8555`。

## 发布门槛与剩余限制

- Windows 安装器取得可信代码签名之前只能发布为 Prerelease。
- 手机摄像连续两小时、100 包裹和 1/5/20/30/45 秒断网恢复属于实机长测，不能由单元测试替代。
- MobSF 和 OWASP ZAP 需要针对最终 Release APK 与实际运行工位地址执行；发现高危或严重问题时不得升级为稳定版。
- 自签名证书由本机保护，重装系统或清理 `%LOCALAPPDATA%\UnpackVision` 后必须重新配对。
