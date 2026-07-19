# 拆包录像系统（UnpackVision）

这是一个独立实现的 Windows 拆包/打包录像与数据同步系统。它不会修改 HIK SCAN，也不包含海康的源代码、商标或专有资源。

## 当前可用能力

- HIK SCAN 兼容同步服务：监控拆包、打包录像目录，等 MP4 写入稳定后解析单号和起止时间，写入 SQLite，并通过可靠重试队列同步 Excel。
- 首次运行安全基线：第一次只登记已有历史录像，不自动补写 Excel；以后产生的新录像才自动进入同步队列。
- Excel 安全写入：按 B 列最后一个真实非空单元格追加，A 列写真实 Excel 日期并继承日期显示格式，B 列强制文本，C 列写平台关联公式，D-F 保持空白；写入前备份，以临时文件和原子替换保存。同步时会安全修复日期列中缺少日期样式的序列值，兼容 Excel 和 WPS。
- 独立录像桌面端：按实际拆包流程独立设计，包含持续实时预览、扫码开始、再次扫描当前单号结束、扫描新单号自动保存并开始下一单、手动停止、重复单号标识、超时提醒、语音提示和右侧最近结果。
- 相机录像：Windows Media Foundation + OpenCV，默认请求 3840x2160、15fps；自动探测满足分辨率的设备并排除不合格的虚拟相机，录像叠加时间与单号水印，先写临时文件，成功关闭编码器后再改为正式 MP4 文件名。
- 视频源选择：主界面可在自动模式和 16 个本地相机序号间快速切换；指定本地相机时接受实际分辨率，支持 USB/UVC、iVCam 等 Windows 摄像头。设置页还支持通用 RTSP/HTTP IPC 视频流，以及按地址、端口、通道和主/子码流配置海康 NVR/DVR；网络密码使用 Windows DPAPI 加密保存。
- 相机控制：自动聚焦、一键聚焦、亮度/对比度/锐度/饱和度、拍照、左转、右转、镜像和全屏预览。
- 录像历史与播放：按单号和日期筛选，显示缩略图、模式、起止时间、时长、大小和同步状态；可播放、打开目录、重试 Excel、导出选中录像和 CSV。
- 单号配置：最短/最长长度、开头与结尾过滤和防误扫间隔。
- 扫码枪隔离：Windows Raw Input 按设备接收扫码，保留输入框作为兼容回退。
- 本地 API：只监听 `127.0.0.1`，使用 DPAPI 保存 API 密钥，提供记录查询、导入、重试、健康检查、图像处理和条码识别接口。
- 离线图像能力：文档自动找边、透视矫正、旋转、彩色/灰度/黑白增强，以及本地一维码、二维码识别。
- 默认不调用云端 OCR。OCR 接口已保留，但未部署本地模型时会明确返回“未配置”，不会上传面单或证件。

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

除 `/api/v1/health` 和 OpenAPI 文档外，请求需携带：

```text
X-UnpackVision-Key: <本机 API 密钥>
```

主要接口：

- `GET /api/v1/records`
- `GET /api/v1/records/{id}`
- `GET /api/v1/records/by-tracking/{trackingNo}`
- `POST /api/v1/records`
- `POST /api/v1/connectors/{id}/retry`
- `GET /api/v1/health`
- `POST /api/v1/images/process`
- `POST /api/v1/images/barcodes`
- `GET /api/v1/ocr/health`

## 数据位置

- SQLite：`%LOCALAPPDATA%\UnpackVision\unpackvision.db`
- 桌面端设置：`%LOCALAPPDATA%\UnpackVision\settings.json`
- 独立录像：`%USERPROFILE%\Videos\UnpackVision\Unpacking` 或 `Packing`
- API 密钥：`%LOCALAPPDATA%\UnpackVision\api-key.protected`

`settings.json` 可在程序关闭时修改扫码规则、停止条码、最大录像分钟数和工作模式。单号在 SQLite 中始终按文本保存，保留字母、横线和前导零。

## 构建、测试和发布

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1
```

发布结果在 `artifacts\publish\App-1.3.1` 和 `artifacts\publish\Service`。构建脚本会执行 Release 编译和全部自动化测试。

## 许可证与独立实现声明

本项目采用 [MIT License](LICENSE)。这是独立实现的软件，不包含 HIK SCAN 或海康威视的源代码、商标、界面素材及其他专有资源；相关产品名称仅用于说明兼容场景。

## 后续扩展点

核心同步接口为 `ISyncConnector`，Excel 只是第一个实现。钉钉 AI 表格和网站后台应各自实现连接器，不需要修改扫码、录像或数据库核心。Webhook、PaddleOCR 模型包、音频轨和虚拟相机尚未作为生产功能交付；相关扩展应在实际设备和业务账号可用后继续验收。
