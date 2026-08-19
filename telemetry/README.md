# 匿名日活服务

本目录包含拆包智录的匿名日活服务。客户端每天最多发送一次不可跨日关联的匿名值，
不发送单号、录像、路径、设备名称、硬件标识或账号。

部署到开发者自己的 Cloudflare 账号：

1. `npm.cmd install`
2. `npx.cmd wrangler login`
3. `npx.cmd wrangler d1 create unpack-vision-dau`
4. 复制返回的 `database_id`
5. `.\deploy.ps1 -DatabaseId <database_id>`
6. 使用部署结果中的 HTTPS 地址设置发布环境变量 `UNPACKVISION_TELEMETRY_ENDPOINT`
7. 单独部署 `unpack-vision-dau-admin`，并在 Cloudflare Zero Trust 中用 Access 保护整个后台 Worker；公开的日活上报 Worker 不受影响
8. 设置 `TEAM_DOMAIN` 和 `POLICY_AUD` Worker 变量；后台还会验证 Access JWT
   的签名、签发方和 Audience，缺少配置时始终返回 `403`

## 开发者统计面板

- `GET /admin?days=7|30|90`：仅供开发者查看的服务端渲染统计页。
- `GET /admin/v1/dau?days=7|30|90`：同一数据的 JSON 接口。

当前后台地址：`https://unpack-vision-dau-admin.chenyuecai520.workers.dev/admin`
- 两个路径都必须经过 Cloudflare Access，且 Worker 会再次校验 Access JWT；其余
  访问者不会获得统计数据。
- “近 N 日”显示每日活跃安装的累计人次。由于匿名值每天重新生成以防跨日追踪，
  它不等同于跨日去重的真实人数。

`wrangler.toml` 包含账号专属数据库 ID，因此不提交仓库。
