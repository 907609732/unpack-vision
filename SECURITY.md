# 安全策略

## 支持范围

仅最新 GitHub Release 版本接受安全修复。未取得 Windows 代码签名证书的构建只作为 Prerelease，不视为推荐稳定版。

## 私密报告漏洞

请使用 GitHub 仓库的 **Security → Report a vulnerability**（Private Vulnerability Reporting）提交。请不要在公开 Issue 中粘贴令牌、配对二维码、完整单号、录像、路径或摄像头密码。

报告建议包含：受影响版本、复现步骤、影响范围和经过脱敏的日志。开发者“五成”会尽快确认；在修复发布前请避免公开利用细节。

## 安全边界

- 管理 API 与 OpenAPI 仅允许本机回环访问。
- 手机控制使用证书固定 HTTPS，媒体发布使用 RTSPS。
- 公开健康接口只返回存活、版本和 TLS 状态。
- 默认不开放公网，不提供云端数据存储。
- 本项目无法替代商家对 Windows 账号、录像目录、工作簿及局域网的访问控制。
