# 2.3.0 上线前安全与发布验证记录

验证日期：2026-07-30

发布结论：自动化门禁通过，版本仅适合继续作为 **Prerelease 候选**。在真实设备长测、最终签名 Android APK 扫描和 Windows 安装/升级验收完成前，不得标记为 Stable。

## 架构与构建

- 新增 `UnpackVision.Application` 应用层，工位扫码路由和录像协调不再放在 Core。
- Core、Application、Infrastructure、Windows 主机和 Android 的边界写入分层 `AGENTS.md` 与 `docs/architecture`。
- ArchUnitNET 自动阻止 Core 依赖 Application/Infrastructure，以及 Application 依赖 Infrastructure。
- .NET Debug 与 Release 均为 0 警告、0 错误，77/77 测试通过。
- Android Debug 单元测试与 APK 构建通过；Release 的 R8 构建通过并生成未签名 APK。
- 匿名日活 Worker 通过 TypeScript 类型检查与 Wrangler dry-run，`npm audit` 未发现漏洞。
- `actionlint 1.7.12` 校验全部 GitHub Actions 工作流通过。

## 接口与媒体烟测

- 基础服务烟测：SQLite 可读写；未授权记录接口返回 401；只绑定回环地址。
- 录像 API 隔离烟测：记录可读取、本地路径不泄露、视频存在；Range 返回 206 和 4 字节内容，并带 ETag。
- StationHost 安全烟测：版本为 2.3.0、TLS 开启、只监听回环与当前私有局域网地址；配对地址使用 HTTPS；错误 Host 返回 400。
- MediaMTX 1.18.2 隔离烟测：进程启动、设备鉴权、RTSPS 发布地址、HTTPS/WHEP 地址和运行时配置均验证通过。
- 烟测使用独立端口和临时数据目录，不接触已安装版本的业务数据。

## 依赖与密钥检查

- `dotnet list UnpackVision.slnx package --vulnerable --include-transitive`：7 个项目均未发现当前 NuGet 源已知漏洞。
- Gitleaks 8.30.1：13 个 Git 提交未发现泄露；源码、测试、脚本、文档、工作流和 Android 源码共 14 个目标未发现泄露。
- 全工作树扫描的 10 条候选全部来自 Android `build/intermediates` 的 Gradle 生成缓存，不属于源码或 Git 历史。
- GitHub 仓库已配置 Android Release 所需的 4 个 Secret；本次只检查 Secret 名称是否存在，未读取 Secret 内容。

## 发布产物

- Windows 2.3.0 自包含发布、Velopack 完整包、差分包、安装器、更新清单和第三方声明均已生成。
- 完整包包含桌面端、StationHost、兼容同步服务和 MediaMTX，共 1546 个条目。
- `desktop-update.json` 版本为 2.3.0，最低支持版本为 2.1.0。
- `SHA256SUMS.txt` 中列出的 7 个文件均已重新计算并匹配。
- Windows 安装器未取得 Authenticode 签名，因此必须维持 Prerelease，并可能触发 SmartScreen。
- 本机没有 Android 签名环境变量；本地生成的 `app-release-unsigned.apk` 经 `apksigner` 验证为未签名，不能发布。正式 APK 必须由已有签名 Secret 的 GitHub 发布工作流生成。

## 仍需人工通过的上线门禁

- 在隔离测试用户或干净 Windows 虚拟机执行首次安装、从 2.2.0 升级到 2.3.0、卸载后业务数据保留，以及自动更新回退验证。
- 使用最终签名 APK 验证全新安装与覆盖升级，并重新配对电脑。
- 真实摄像头、扫码枪和手机连续运行至少两小时，完成至少 100 个包裹。
- 分别模拟 1、5、20、30、45 秒断网，确认录像、单号、Excel 和重连状态一致。
- 对最终签名 APK 执行 MobSF；对实际隔离工位地址执行 OWASP ZAP。发现高危或严重问题时不得上线。
