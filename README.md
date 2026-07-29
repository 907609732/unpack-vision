# 电商拆包智能录像

<p align="center">
  <img src="docs/assets/logo.png" width="180" alt="电商拆包智能录像 Logo">
</p>

这是一个面向中小商家的零成本、开源 Windows 拆包/打包录像、手机协同与数据同步系统。它不会修改 HIK SCAN，也不包含海康的源代码、商标或专有资源。

## 下载

- [Windows 安装器](https://github.com/907609732/unpack-vision/releases/latest/download/EcommerceUnpackRecorder-win-Setup.exe)
- [安卓手机 APP](https://github.com/907609732/unpack-vision/releases/latest/download/EcommerceUnpackRecorder-Android.apk)
- [最新版本与更新说明](https://github.com/907609732/unpack-vision/releases/latest)
- [源码仓库](https://github.com/907609732/unpack-vision)

Windows 端使用 Velopack 每用户安装器，后续更新由软件后台下载并在录像空闲、用户确认后安装，不需要手工覆盖旧文件。当前安装器尚未取得代码签名证书，只作为 Prerelease 提供，首次下载可能出现 SmartScreen 提示；Release 同时提供 SHA256 和 GitHub 构建来源证明。

## 当前可用能力

- HIK SCAN 兼容同步服务：监控拆包、打包录像目录，等 MP4 写入稳定后解析单号和起止时间，写入 SQLite，并通过可靠重试队列同步 Excel。
- 首次运行安全基线：第一次只登记已有历史录像，不自动补写 Excel；以后产生的新录像才自动进入同步队列。
- Excel 安全写入：按 B 列最后一个真实非空单元格追加，A 列写真实 Excel 日期并继承日期显示格式，B 列强制文本，C 列写平台关联公式，E 列写入 `【电商拆包智能录像】` 来源标记并安全合并异常/备注，D、F 保持空白；写入前备份，以临时文件和原子替换保存。同步时会安全修复日期列中缺少日期样式的序列值，兼容 Excel 和 WPS。
- 独立录像桌面端：按实际拆包流程独立设计，包含持续实时预览、扫码开始、再次扫描当前单号结束、扫描新单号自动保存并开始下一单、手动停止、重复单号标识、超时提醒、语音提示和右侧最近结果。
- 异常标签与备注：录像中可扫描固定“破损”“调包”指令条码或点击快捷按钮，一个包裹可有多个标签并可撤销；备注 500ms 自动保存。异常会进入实时水印、录像文件名、SQLite、历史检索与 Excel E 列，人工 Excel 备注不会被覆盖。
- 热敏条码设计器：录像不中断即可切换页面；支持文本、图片/Logo、Code 128、二维码、直线、矩形、拖动缩放、旋转、图层、对齐、复制粘贴、撤销重做、毫米尺寸、打印偏移、Excel/CSV 字段映射、批量份数、版本化 JSON 模板和 Windows 打印机驱动。
- 相机录像：Windows Media Foundation + OpenCV，默认请求 3840x2160、15fps；自动探测满足分辨率的设备并排除不合格的虚拟相机，录像叠加时间与单号水印，先写临时文件，成功关闭编码器后再改为正式 MP4 文件名。
- 视频源选择：主界面可在自动模式和 16 个本地相机序号间快速切换；指定本地相机时接受实际分辨率，支持 USB/UVC、iVCam 等 Windows 摄像头。设置页还支持通用 RTSP/HTTP IPC 视频流，以及按地址、端口、通道和主/子码流配置海康 NVR/DVR；网络密码使用 Windows DPAPI 加密保存。
- 相机控制：自动聚焦、一键聚焦、亮度/对比度/锐度/饱和度、拍照、左转、右转、镜像和全屏预览。
- 录像历史与播放：按单号、备注、日期和异常标签筛选，显示缩略图、模式、起止时间、时长、大小和同步状态；可编辑异常/备注，播放时点击异常时间点直接跳转。支持打开目录、重试 Excel、导出录像/CSV、Ctrl/Shift 多选、全选和批量删除。
- 单号配置：最短/最长长度、开头与结尾过滤和防误扫间隔。
- 扫码枪隔离：Windows Raw Input 按设备接收扫码，保留输入框作为兼容回退。
- 本地 API：只监听 `127.0.0.1`，使用 DPAPI 保存 API 密钥，提供记录查询、导入、重试、健康检查、图像处理和条码识别接口。
- 离线图像能力：文档自动找边、透视矫正、旋转、彩色/灰度/黑白增强，以及本地一维码、二维码识别。
- 默认不调用云端 OCR。OCR 接口已保留，但未部署本地模型时会明确返回“未配置”，不会上传面单或证件。

## 2.2 隐私、安全与安卓协同

2.2.0 已完成可运行的局域网协同主链路与首轮公开发布安全加固：

- 新增常驻用户会话的 `UnpackVision.StationHost`，统一接收电脑、手机和未来网站的扫码命令。
- 新增幂等 `IScanCommandRouter`；命令回执按幂等键保存在 SQLite，工位主机重启后重复事件仍返回原结果。手机扫码器可选择是否触发录像，关闭时可靠追加 Excel。
- 安卓端提供“智能摄像头”和“手机扫码器”两种主模式，异常遥控器已合并到扫码页面。
- 异常遥控器已接通当前录像，可查看当前单号和录像状态，并执行破损、调包、撤销标签、问题截图、备注和停止录像；电脑确认后手机提供中文语音与震动反馈。
- CameraX 同一摄像头会话同时向 RootEncoder 2.8.0 提供 H.264 推流画面，并向 ZXing-C++ 3.1.0 提供离线条码识别帧。
- 已实现“三次稳定识别后发送、离开扫码区 1.2 秒后才允许同码再次触发”的防重复规则。
- 桌面设置页可生成五分钟有效的一次性配对二维码，并允许在多网卡电脑上明确选择手机所在的局域网地址。
- 每台手机生成独立密钥并用 Android Keystore 保护工位令牌；电脑数据库只保存令牌摘要。桌面端可查看已配对设备、角色、权限和最近在线时间，也可永久删除设备并立即断开其媒体会话。
- MediaMTX 1.18.2 按需启动，手机自动申请设备专属 RTSPS 推流路径；发布和读取分别验证 `camera:publish`、`video:read`，同时提供 HTTPS/WHEP 实时预览地址。
- 工位主机已提供游标分页记录查询、记录事件时间线、缩略图和视频读取接口；视频支持 HTTP Range/206、ETag 和断点播放，接口只返回业务字段，不暴露电脑本机文件路径。
- 局域网调试监听只绑定回环地址和当前可用的私有 IPv4 地址，不再占用所有网卡；配对页会排除 Windows 已失效或处于 Deprecated 状态的地址。
- 2.0.1 增加 DNS-SD/mDNS 工位自动发现：保存的 IP 失效后，安卓端会依次尝试局域网自动发现、手机热点、USB/蓝牙网络共享及 USB 调试反向通道。
- 2.0.2 修复相机扫码及异常遥控器保存旧连接状态的问题；所有发送入口改为读取实时连接状态，命令结果不再被定时刷新覆盖。
- 电脑端在当前 Windows 用户登录后自动启动；手机会分别检测“工位服务在线”和“桌面录像程序已就绪”，不会再把只能连接但无法录像的状态显示为成功。
- MediaMTX 由校验 SHA256 的脚本下载，不把第三方二进制提交到源码仓库；发布包会携带已校验二进制和上游许可证。

2.2.0 提供固定 Release 签名 APK、GitHub Release 更新清单和 SHA256 校验。手机控制接口使用工位自签名证书的 HTTPS 并在安卓端固定 SHA256 指纹；媒体发布使用 RTSPS。旧明文配对凭据会在安全迁移时失效，需要重新扫码配对一次。USB 调试兜底只允许 `127.0.0.1` 回环明文，不允许局域网明文连接。

首次启动会在相机、扫码和联网更新之前展示《用户协议》和《隐私政策》。2.2.0 的匿名统计接口为空实现，不生成稳定安装 ID；开发者只参考 GitHub Release 资源下载次数。设置页可查看协议、隐私政策、安全报告与“支持作者”，未配置真实赞赏码时不会显示测试二维码。

工位主机首批开放接口：

- `GET /api/v1/records`（游标分页，可按单号筛选）
- `GET /api/v1/records/{id}`
- `GET /api/v1/records/{id}/thumbnail`
- `GET /api/v1/records/{id}/video`（支持 Range/206 与 ETag）
- `GET /api/v1/records/{id}/events`
- `GET /api/v1/devices`
- `POST /api/v1/devices/{id}/revoke`
- `DELETE /api/v1/devices/{id}`
- `GET /api/v1/stations/{id}/state`
- `POST /api/v1/stations/{id}/scans`
- `GET /api/v1/stations/{id}/live`

## 安全约定

- 兼容同步服务可以和 HIK SCAN 同时运行，它只读录像目录。
- 独立录像桌面端需要独占相机。使用它录像时应先退出 HIK SCAN，避免相机被占用。
- 首次试运行建议复制 Excel 工作簿，并通过环境变量 `Excel__WorkbookPath` 指向副本。
- 正式工作簿被 Excel 或 Plastic SCM 占用时，任务会等待并自动重试，不会强行写入。
- Excel 恢复副本默认保存在 `%LOCALAPPDATA%\UnpackVision\Backups`。

## 快速启动

需要 Windows 10/11 和 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。脚本会优先使用项目根目录的 `.dotnet`，不存在时使用系统安装的 SDK：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\run-service.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\run-app.ps1
```

`run-service.ps1` 启动 HIK 目录监控、SQLite 和 Excel 同步。`run-app.ps1` 启动独立录像桌面端。已经生成发布包时，也可以直接双击根目录的 `Start-Sync-Service.cmd` 和 `Start-UnpackVision.cmd`。

首次使用桌面端：

1. 退出 HIK SCAN，确认摄像头未被其他程序占用。
2. 打开桌面端，选择“拆包”或“打包”。
3. 扫描普通快递单号开始录像，界面会显示当前单号条码。
4. 拆包完成后再次扫描当前单号结束并保存；也可以直接扫描下一个快递单号，软件会保存当前录像并自动开始下一单。
5. 扫码枪异常时使用“手动结束录像”。

## 兼容同步服务配置

默认值位于 `src/UnpackVision.Service/appsettings.json`。生产环境推荐用环境变量覆盖，双下划线代表配置层级：

```powershell
$env:Excel__WorkbookPath = 'E:\path\退货扫码记录.xlsx'
$env:HikCompatibility__UnpackingDirectory = 'D:\Program Files\海康威视\HIK SCAN\storage\LogisticsUnpacking'
$env:HikCompatibility__PackingDirectory = 'D:\Program Files\海康威视\HIK SCAN\storage\LogisticsPaking'
powershell -ExecutionPolicy Bypass -File .\scripts\run-service.ps1
```

查看本机 API 密钥：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-service.ps1 -ShowApiKey
```

除最小化的 `/api/v1/health` 外，请求需携带：

```text
X-UnpackVision-Key: <本机 API 密钥>
```

主要接口：

- `GET /api/v1/records`
- `GET /api/v1/records/{id}`
- `GET /api/v1/records/by-tracking/{trackingNo}`
- `GET /api/v1/tags`
- `POST /api/v1/records/{id}/tags/{tagId}`
- `DELETE /api/v1/records/{id}/tags/{assignmentId}`
- `PUT /api/v1/records/{id}/note`
- `POST /api/v1/records`
- `POST /api/v1/connectors/{id}/retry`
- `GET /api/v1/health`
- `POST /api/v1/images/process`
- `POST /api/v1/images/barcodes`
- `GET /api/v1/ocr/health`

## 数据位置

- SQLite：`%LOCALAPPDATA%\UnpackVision\unpackvision.db`
- 桌面端设置：`%LOCALAPPDATA%\UnpackVision\settings.json`
- 热敏标签模板：`%LOCALAPPDATA%\UnpackVision\Templates`
- 独立录像：`%USERPROFILE%\Videos\UnpackVision\Unpacking` 或 `Packing`
- API 密钥：`%LOCALAPPDATA%\UnpackVision\api-key.protected`

`settings.json` 保存扫码规则、异常条码、最大录像分钟数和工作模式。单号在 SQLite 中始终按文本保存，保留字母、横线和前导零。

## 构建、测试和发布

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1
```

构建安卓 Debug APK（脚本会检查系统 Android Studio/JBR 和 SDK，并规避 Windows 中文路径导致的 Gradle 测试问题）：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-android.ps1
```

开发调试设备还可通过 USB/无线 ADB 建立 `127.0.0.1:5271` 反向通道，作为路由器或热点不可用时的最后兜底：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\enable-adb-usb-fallback.ps1
```

APK 输出到 `mobile\UnpackVision.Android\app\build\outputs\apk\debug\app-debug.apk`。下载固定版本 MediaMTX：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\fetch-mediamtx.ps1
```

正式工位不提供局域网 HTTP 开关。开发调试的明文兜底仅通过 ADB 反向映射到手机自己的 `127.0.0.1`，不能从局域网访问。

发布结果保存在 `artifacts\release-output`。Windows 安装器把桌面端、`StationHost`、兼容同步服务和带许可证的 MediaMTX 作为同一版本整体更新；安卓端生成固定文件名 APK、更新清单和 SHA256。

生成完整 2.2.0 发布文件：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-signed-android.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\package-release.ps1 `
  -Version 2.2.0 `
  -AndroidApk .\mobile\UnpackVision.Android\app\build\outputs\apk\release\app-release.apk
```

## 许可证与独立实现声明

本项目采用 [MIT License](LICENSE)。这是独立实现的软件，不包含 HIK SCAN 或海康威视的源代码、商标、界面素材及其他专有资源；相关产品名称仅用于说明兼容场景。

隐私与安全资料：

- [用户协议](docs/TERMS.md)
- [隐私政策](docs/PRIVACY.md)
- [安全报告与漏洞披露](SECURITY.md)
- [2.2.0 安全审计记录](docs/SECURITY-AUDIT-2.2.0.md)

## 后续扩展点

核心同步接口为 `ISyncConnector`，Excel 只是第一个实现。钉钉 AI 表格和网站后台应各自实现连接器，不需要修改扫码、录像或数据库核心。服务端可配置带事件 ID、时间戳和 HMAC-SHA256 签名的 Webhook；PaddleOCR 模型包、音频轨和虚拟相机仍需在实际设备可用后继续验收。
