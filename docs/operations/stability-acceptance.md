# 拆包智录 8/16 路稳定版验收手册

2.5.1 仍是预览版。只有本手册所列的实体机证据全部完成，且 Windows 安装器取得可信代码签名后，才可以把对应版本标记为 Stable。源代码支持 16 路、60 秒性能测试通过或应用运行了两小时，都不能单独替代完整验收。

## 验收工具的安全边界

`scripts/run-stability-acceptance.ps1` 是只读监测工具：

- 只读 SQLite、Excel 隐藏同步标记和录像元数据。
- 不生成单号、不替操作员扫码、不修改 Excel、不删除或移动录像。
- 不会写满、格式化、弹出或卸载任何硬盘。
- 报告不保存单号、摄像头密码、RTSP 地址或完整业务路径。
- 每次会话目录中的 `session-private.json` 包含本机路径，仅用于本机复现，不能上传到 GitHub 或发送给其他人。

默认报告位置为：

```text
%LOCALAPPDATA%\UnpackVision\Acceptance\日期时间-模式
```

其中 `report.md` 是可阅读结果，`resources.csv` 是资源采样，`events.jsonl` 记录程序退出和盘位上下线，`start-snapshot.json` 与 `final-snapshot.json` 是脱敏的前后快照。

## 验收前准备

1. 安装待验收版本并确认版本号、StationHost 健康状态和数据升级正常。
2. 在“系统设置 → 相机信息”建立对应的 8 路或 16 路多机位方案。
3. 运行软件内 60 秒真实性能测试并保存结果截图。8/16 路必须使用 GStreamer 1.28.5 和可用的硬件 H.264 编码器。
4. 确认 Excel 已绑定且当前没有同步失败任务。
5. 确认所有录像盘位健康，预计空间足够覆盖两小时和安全保留量。
6. 关闭会改变 CPU/GPU/磁盘负载的非必要程序，保持 Windows 电源模式、网络和摄像头供电不变。

当前主链仍是 OpenCV 采集后交给 GStreamer 编码。报告必须如实写明这一点；在原生 GStreamer `tee + queue` 采集链完全替换并重新验收前，不得把它描述为已完成。

## 第一轮：8 路连续两小时

先在软件中选择并测试 8 路方案，启动桌面程序但不要提前录像。打开一个独立 PowerShell 窗口运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-stability-acceptance.ps1 `
  -Mode EightCamera `
  -DurationMinutes 120 `
  -ExpectedCameraCount 8 `
  -ProbeMedia `
  -StopWhenDesktopExits
```

在进度条运行期间按正常业务节奏扫码、拆包和停止。脚本结束后检查 `report.md`，并把软件内 60 秒性能结果截图放入同一会话目录。必须同时满足：

- 8 路均有独立原片，主机位没有失败。
- 平均视频帧率至少为目标的 90%。
- 单路运行期丢帧率低于 1%（以软件性能窗口为证）。
- 各路时间漂移不超过 100ms。
- CPU、GPU和磁盘至少保留 25% 余量。
- 没有缺失、零字节、不可播放文件或未恢复媒体缺口。

## 第二轮：16 路连续两小时

把相机方案切换为 16 路并重新执行 60 秒性能测试。不要复用 8 路报告：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-stability-acceptance.ps1 `
  -Mode SixteenCamera `
  -DurationMinutes 120 `
  -ExpectedCameraCount 16 `
  -ProbeMedia `
  -StopWhenDesktopExits
```

验收阈值与 8 路相同。使用子码流、720p 或 10fps 是允许的，但实际配置和性能测试结果必须保存；不能在录像过程中静默改变原片画质。

## 第三轮：100 个包裹完整对账

开始前确保 Excel 同步队列为空。运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-stability-acceptance.ps1 `
  -Mode HundredPackage `
  -DurationMinutes 240 `
  -ExpectedPackages 100 `
  -ExpectedCameraCount 8 `
  -ProbeMedia `
  -StopWhenDesktopExits
```

`DurationMinutes` 只需覆盖实际处理时间，可以按现场速度调整。完成后核对：

- SQLite 完成记录恰好对应本轮 100 个包裹；重复单号仍是独立业务记录。
- 每条完成记录具有预期数量的独立机位资产。
- 所有媒体文件存在、非零并可被 ffprobe 打开。
- Excel `__UnpackVisionSync` 中存在每条完成记录的 RecordId。
- 同步失败、等待任务均为零；工作簿被占用时的任务最终已补写。

脚本按 RecordId 对账，不把真实单号写入报告。

## 第四轮：USB、IPC、NVR、手机混合来源

在相机方案中至少各启用一条真实 USB、IPC、海康 NVR 通道和安卓手机流，再运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-stability-acceptance.ps1 `
  -Mode MixedSource `
  -DurationMinutes 120 `
  -ExpectedCameraCount 8 `
  -ProbeMedia
```

报告只能自动识别 Windows、网络流和海康来源的大类；手机流如果以通用 RTSP 接入，会计入网络流。因此还要在报告旁保存一张不含密码和完整地址的机位配置截图。

测试期间分别进行一次：IPC 断网、NVR 通道断流、手机 Wi-Fi 短时中断和 USB 副机位重插。主机位故障应使整单失败；副机位故障应保留其他原片并形成准确媒体缺口，不得让其他管线停住。

## 第五轮：多硬盘拔盘、换盘和空间不足

只使用专门的备用测试盘，不要对系统盘、真实业务唯一副本或正在写入的重要录像执行拔盘测试。启动监测：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-stability-acceptance.ps1 `
  -Mode StoragePool `
  -DurationMinutes 60
```

按顺序人工验证：

1. 两块健康盘按优先级分配，整单所有资产始终落在同一盘。
2. 第一盘进入黄色预警后，下一单切换到第二盘，当前单不跨盘。
3. 在没有录像的间隙拔出备用盘，软件标记离线；换盘符重新接入后按卷 ID 恢复。
4. 使用可删除的测试 VHD 或磁盘配额模拟空间不足，不要向真实盘写垃圾数据直到 0 字节。
5. 全部盘位低于安全线时，下一单被拒绝；已有录像和数据库保持完整。
6. 只读盘、盘位排序、禁用盘位和旧 `RecordingRoot` 兼容分别留证。

脚本仅记录盘位在线状态和剩余空间变化，不会自动执行上述破坏性动作。

## 第六轮：真实低配电脑

必须在第二台符合目标最低配置的真实电脑上重复 4 路和该机器获准的最高路数测试。限制当前高配电脑的进程优先级、CPU 亲和性或内存，不能代替真实低配电脑的 USB 控制器、核显、内存带宽和磁盘行为。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-stability-acceptance.ps1 `
  -Mode LowEnd `
  -DurationMinutes 120 `
  -ExpectedCameraCount 4 `
  -ProbeMedia
```

保存硬件摘要、性能测试结果和验收报告。未通过时按照“子码流 → 720p → 10fps → 降低预览 → 减少路数”的顺序调整，并在每次改动后重新运行 60 秒性能测试。

## 判定原则

- `通过`：脚本可观察的项目全部满足，没有待补证项。
- `待补证`：数据没有直接失败，但 GPU、运行期丢帧或手工故障步骤尚缺证据。
- `未通过`：时间未跑满、程序退出、SQLite 不可读、包裹数不足、媒体缺失、Excel 对账缺失或媒体指标超过阈值。

每个模式使用独立会话目录，不覆盖旧报告。只有 8 路、16 路、100 包裹、混合来源、多盘和低配电脑六类报告均完成，才满足稳定版设备验收门槛。
