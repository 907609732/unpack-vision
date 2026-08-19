import { createRemoteJWKSet, jwtVerify } from "jose";

interface Env {
  DB: D1Database;
  RATE_LIMITER: RateLimit;
  TEAM_DOMAIN?: string;
  POLICY_AUD?: string;
}

interface DailyActiveRequest {
  day: string;
  dailyId: string;
  platform: "windows" | "android";
  appVersion: string;
  channel: "stable" | "prerelease";
}

interface DailyTotal {
  day: string;
  platform: "windows" | "android";
  activeCount: number;
}

interface DailyVersion {
  day: string;
  platform: "windows" | "android";
  appVersion: string;
  activeCount: number;
}

interface DashboardSummary {
  generatedAt: string;
  days: number;
  totals: DailyTotal[];
  versions: DailyVersion[];
}

const jsonHeaders = {
  "content-type": "application/json; charset=utf-8",
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
  "referrer-policy": "no-referrer"
};

const htmlHeaders = {
  ...jsonHeaders,
  "content-type": "text/html; charset=utf-8",
  "content-security-policy": "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
  "permissions-policy": "camera=(), microphone=(), geolocation=(), payment=(), usb=()",
  "x-frame-options": "DENY"
};

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    if (request.method === "POST" && url.pathname === "/v1/dau") {
      return recordDailyActive(request, env);
    }
    if (request.method === "GET" && url.pathname === "/admin/v1/dau") {
      return adminSummary(request, env);
    }
    if (request.method === "GET" && url.pathname === "/admin") {
      return adminDashboard(request, env);
    }
    return new Response("Not found", { status: 404, headers: jsonHeaders });
  },

  async scheduled(_controller: ScheduledController, env: Env): Promise<void> {
    await env.DB.batch([
      env.DB.prepare("DELETE FROM daily_active WHERE day < date('now', '-35 days')"),
      env.DB.prepare("DELETE FROM daily_totals WHERE day < date('now', '-730 days')"),
      env.DB.prepare("DELETE FROM daily_versions WHERE day < date('now', '-730 days')")
    ]);
  }
} satisfies ExportedHandler<Env>;

async function recordDailyActive(request: Request, env: Env): Promise<Response> {
  if (request.headers.get("origin")) {
    return json({ error: "browser requests are not accepted" }, 403);
  }
  const contentType = request.headers.get("content-type") ?? "";
  if (!contentType.toLowerCase().startsWith("application/json")) {
    return json({ error: "application/json required" }, 415);
  }
  const declaredLength = Number(request.headers.get("content-length") ?? "0");
  if (declaredLength > 1024) {
    return json({ error: "request too large" }, 413);
  }
  const remoteKey = request.headers.get("cf-connecting-ip") ?? "unknown";
  const rate = await env.RATE_LIMITER.limit({ key: `dau:${remoteKey}` });
  if (!rate.success) {
    return json({ error: "rate limited" }, 429);
  }

  const bodyText = await request.text();
  if (new TextEncoder().encode(bodyText).byteLength > 1024) {
    return json({ error: "request too large" }, 413);
  }
  let body: unknown;
  try {
    body = JSON.parse(bodyText);
  } catch {
    return json({ error: "invalid json" }, 400);
  }
  if (!isDailyActiveRequest(body) || !isAllowedDay(body.day)) {
    return json({ error: "invalid request" }, 400);
  }

  const inserted = await env.DB.prepare(
    `INSERT OR IGNORE INTO daily_active
       (day, daily_id, platform, app_version, channel, created_at)
     VALUES (?1, ?2, ?3, ?4, ?5, datetime('now'))`
  ).bind(body.day, body.dailyId.toUpperCase(), body.platform, body.appVersion, body.channel).run();

  if ((inserted.meta.changes ?? 0) > 0) {
    await env.DB.batch([
      env.DB.prepare(
        `INSERT INTO daily_totals(day, platform, active_count)
         VALUES (?1, ?2, 1)
         ON CONFLICT(day, platform)
         DO UPDATE SET active_count = active_count + 1`
      ).bind(body.day, body.platform),
      env.DB.prepare(
        `INSERT INTO daily_versions(day, platform, app_version, active_count)
         VALUES (?1, ?2, ?3, 1)
         ON CONFLICT(day, platform, app_version)
         DO UPDATE SET active_count = active_count + 1`
      ).bind(body.day, body.platform, body.appVersion)
    ]);
  }
  return new Response(null, { status: 204, headers: jsonHeaders });
}

async function adminSummary(request: Request, env: Env): Promise<Response> {
  if (!await hasValidAccessIdentity(request, env)) {
    return json({ error: "Cloudflare Access required" }, 403);
  }
  const url = new URL(request.url);
  const dashboard = await loadDashboardSummary(env, parseDays(url.searchParams.get("days")));
  return json(dashboard);
}

async function adminDashboard(request: Request, env: Env): Promise<Response> {
  if (!await hasValidAccessIdentity(request, env)) {
    return new Response("Access required", { status: 403, headers: htmlHeaders });
  }
  const url = new URL(request.url);
  const dashboard = await loadDashboardSummary(env, parseDays(url.searchParams.get("days")));
  return new Response(renderDashboard(dashboard), { headers: htmlHeaders });
}

async function loadDashboardSummary(env: Env, days: number): Promise<DashboardSummary> {
  // D1's date('now') is UTC. Use the same Beijing-day convention as client payloads.
  const startDay = dateDaysAgo(days - 1);
  const totals = await env.DB.prepare(
    `SELECT day, platform, active_count AS activeCount
     FROM daily_totals
     WHERE day >= ?1
     ORDER BY day DESC, platform`
  ).bind(startDay).all();
  const versions = await env.DB.prepare(
    `SELECT day, platform, app_version AS appVersion, active_count AS activeCount
     FROM daily_versions
     WHERE day >= ?1
     ORDER BY day DESC, platform, active_count DESC`
  ).bind(startDay).all();
  return {
    generatedAt: new Date().toISOString(),
    days,
    totals: totals.results.map(toDailyTotal).filter((item): item is DailyTotal => item !== null),
    versions: versions.results.map(toDailyVersion).filter((item): item is DailyVersion => item !== null)
  };
}

function parseDays(value: string | null): number {
  const requestedDays = Number(value ?? "30");
  return Number.isInteger(requestedDays) ? Math.min(Math.max(requestedDays, 1), 90) : 30;
}

function toDailyTotal(value: Record<string, unknown>): DailyTotal | null {
  if (typeof value.day !== "string" || typeof value.activeCount !== "number" ||
      (value.platform !== "windows" && value.platform !== "android")) return null;
  return { day: value.day, platform: value.platform, activeCount: value.activeCount };
}

function toDailyVersion(value: Record<string, unknown>): DailyVersion | null {
  if (typeof value.day !== "string" || typeof value.appVersion !== "string" ||
      typeof value.activeCount !== "number" ||
      (value.platform !== "windows" && value.platform !== "android")) return null;
  return { day: value.day, platform: value.platform, appVersion: value.appVersion, activeCount: value.activeCount };
}

function renderDashboard(summary: DashboardSummary): string {
  const totalFor = (days: number, platform?: DailyTotal["platform"]) => summary.totals
    .filter((item) => !platform || item.platform === platform)
    .filter((item) => item.day >= dateDaysAgo(days - 1))
    .reduce((sum, item) => sum + item.activeCount, 0);
  const today = beijingDay();
  const todayTotal = summary.totals
    .filter((item) => item.day === today)
    .reduce((sum, item) => sum + item.activeCount, 0);
  const points = dailyPoints(summary.totals, summary.days);
  const max = Math.max(1, ...points.map((item) => item.total));
  const width = 720;
  const height = 190;
  const chartPoints = points.map((point, index) => {
    const x = points.length === 1 ? width / 2 : 18 + index * ((width - 36) / (points.length - 1));
    const y = height - 25 - (point.total / max) * (height - 55);
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(" ");
  const versions = summary.versions.slice(0, 20).map((item) =>
    `<tr><td>${escapeHtml(item.day)}</td><td>${item.platform === "windows" ? "Windows" : "Android"}</td><td>${escapeHtml(item.appVersion)}</td><td>${item.activeCount}</td></tr>`
  ).join("") || "<tr><td colspan=\"4\" class=\"empty\">尚无版本统计</td></tr>";
  const labels = points.filter((_, index) => index === 0 || index === points.length - 1 || index % 7 === 0).map((point, index) =>
    `<span style=\"left:${((points.findIndex((item) => item.day === point.day) / Math.max(1, points.length - 1)) * 100).toFixed(1)}%\">${escapeHtml(point.day.slice(5))}</span>`
  ).join("");
  return `<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>拆包智录 · 匿名日活统计</title><style>
    :root{color-scheme:light;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI","Microsoft YaHei",sans-serif;color:#1d1d1f;background:#f5f7fb}.page{max-width:1040px;margin:0 auto;padding:42px 24px 64px}.eyebrow{color:#3478f6;font-size:13px;font-weight:700;letter-spacing:.08em}.title{font-size:34px;letter-spacing:-.04em;margin:8px 0}.sub{color:#6e6e73;margin:0 0 24px}.nav{display:flex;gap:8px;margin-bottom:22px}.nav a{padding:8px 13px;border-radius:99px;background:#fff;color:#3478f6;text-decoration:none;border:1px solid #e4e8f0}.nav a.active{color:#fff;background:#3478f6;border-color:#3478f6}.grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:14px}.card,.panel{background:rgba(255,255,255,.92);border:1px solid #e5e8ef;border-radius:18px;box-shadow:0 10px 30px rgba(36,55,88,.06)}.card{padding:18px}.label{font-size:13px;color:#6e6e73}.value{font-size:30px;font-weight:700;margin-top:8px}.panel{padding:22px;margin-top:16px}.panel h2{font-size:16px;margin:0 0 16px}.chart{width:100%;height:auto;display:block;background:linear-gradient(180deg,#f7faff,#fff);border-radius:12px}.labels{height:20px;position:relative;color:#8c8c93;font-size:11px}.labels span{position:absolute;transform:translateX(-50%);top:5px;white-space:nowrap}table{width:100%;border-collapse:collapse;font-size:14px}th,td{text-align:left;padding:11px 8px;border-bottom:1px solid #edf0f4}th{color:#6e6e73;font-weight:600}.empty{text-align:center;color:#8c8c93;padding:24px}.foot{font-size:12px;color:#8c8c93;margin-top:20px;line-height:1.6}@media(max-width:720px){.grid{grid-template-columns:repeat(2,minmax(0,1fr))}.page{padding:24px 14px}.title{font-size:28px}}</style></head><body><main class=\"page\"><div class=\"eyebrow\">拆包智录 · 开发者后台</div><h1 class=\"title\">匿名日活统计</h1><p class=\"sub\">仅聚合安装活跃数；不展示单号、录像、路径、设备标识或账号。</p><nav class=\"nav\"><a href=\"/admin?days=7\" class=\"${summary.days === 7 ? "active" : ""}\">近 7 日</a><a href=\"/admin?days=30\" class=\"${summary.days === 30 ? "active" : ""}\">近 30 日</a><a href=\"/admin?days=90\" class=\"${summary.days === 90 ? "active" : ""}\">近 90 日</a></nav><section class=\"grid\"><article class=\"card\"><div class=\"label\">今日活跃安装</div><div class=\"value\">${todayTotal}</div></article><article class=\"card\"><div class=\"label\">近 ${summary.days} 日活跃人次</div><div class=\"value\">${totalFor(summary.days)}</div></article><article class=\"card\"><div class=\"label\">Windows</div><div class=\"value\">${totalFor(summary.days, "windows")}</div></article><article class=\"card\"><div class=\"label\">Android</div><div class=\"value\">${totalFor(summary.days, "android")}</div></article></section><section class=\"panel\"><h2>每日趋势</h2><svg class=\"chart\" viewBox=\"0 0 ${width} ${height}\" role=\"img\" aria-label=\"每日匿名活跃安装趋势\"><line x1=\"18\" y1=\"${height - 25}\" x2=\"${width - 18}\" y2=\"${height - 25}\" stroke=\"#dfe5ef\"/><polyline fill=\"none\" stroke=\"#3478f6\" stroke-width=\"4\" stroke-linecap=\"round\" stroke-linejoin=\"round\" points=\"${chartPoints}\"/>${points.map((point, index) => { const [x,y] = chartPoints.split(" ")[index].split(","); return `<circle cx=\"${x}\" cy=\"${y}\" r=\"4\" fill=\"#3478f6\"><title>${escapeHtml(point.day)}：${point.total}</title></circle>`; }).join("")}</svg><div class=\"labels\">${labels}</div></section><section class=\"panel\"><h2>版本分布</h2><table><thead><tr><th>日期</th><th>平台</th><th>版本</th><th>活跃安装</th></tr></thead><tbody>${versions}</tbody></table></section><p class=\"foot\">生成时间：${escapeHtml(formatBeijing(summary.generatedAt))}（北京时间）。为保护隐私，跨日匿名值不可关联；近 ${summary.days} 日为每日活跃安装累计人次，不代表跨日去重人数。本页仅在 Cloudflare Access 验证通过后提供。</p></main></body></html>`;
}

function dailyPoints(totals: DailyTotal[], days: number): Array<{ day: string; total: number }> {
  const from = dateDaysAgo(days - 1);
  const byDay = new Map<string, number>();
  for (const item of totals) byDay.set(item.day, (byDay.get(item.day) ?? 0) + item.activeCount);
  const result: Array<{ day: string; total: number }> = [];
  for (let offset = days - 1; offset >= 0; offset--) {
    const day = dateDaysAgo(offset);
    if (day >= from) result.push({ day, total: byDay.get(day) ?? 0 });
  }
  return result;
}

function beijingDay(): string {
  return new Date(Date.now() + 8 * 60 * 60 * 1000).toISOString().slice(0, 10);
}

function dateDaysAgo(days: number): string {
  const date = new Date(Date.now() + 8 * 60 * 60 * 1000 - days * 24 * 60 * 60 * 1000);
  return date.toISOString().slice(0, 10);
}

function formatBeijing(value: string): string {
  return new Intl.DateTimeFormat("zh-CN", { dateStyle: "medium", timeStyle: "medium", timeZone: "Asia/Shanghai" }).format(new Date(value));
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>\"']/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" })[character]!);
}

async function hasValidAccessIdentity(request: Request, env: Env): Promise<boolean> {
  const token = request.headers.get("cf-access-jwt-assertion");
  if (!token || token.length > 8192 || !env.TEAM_DOMAIN || !env.POLICY_AUD) return false;
  try {
    const teamDomain = new URL(env.TEAM_DOMAIN);
    if (teamDomain.protocol !== "https:" ||
        !teamDomain.hostname.endsWith(".cloudflareaccess.com")) return false;
    const jwks = createRemoteJWKSet(
      new URL("/cdn-cgi/access/certs", teamDomain.origin)
    );
    await jwtVerify(token, jwks, {
      issuer: teamDomain.origin,
      audience: env.POLICY_AUD
    });
    return true;
  } catch {
    return false;
  }
}

function isDailyActiveRequest(value: unknown): value is DailyActiveRequest {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const record = value as Record<string, unknown>;
  const allowedKeys = new Set(["day", "dailyId", "platform", "appVersion", "channel"]);
  if (Object.keys(record).some((key) => !allowedKeys.has(key)) || Object.keys(record).length !== 5) return false;
  return typeof record.day === "string" &&
    /^\d{4}-\d{2}-\d{2}$/.test(record.day) &&
    typeof record.dailyId === "string" &&
    /^[0-9a-f]{64}$/i.test(record.dailyId) &&
    (record.platform === "windows" || record.platform === "android") &&
    typeof record.appVersion === "string" &&
    /^[0-9A-Za-z.+-]{1,32}$/.test(record.appVersion) &&
    (record.channel === "stable" || record.channel === "prerelease");
}

function isAllowedDay(day: string): boolean {
  const now = new Date();
  const beijing = new Date(now.getTime() + 8 * 60 * 60 * 1000);
  const today = beijing.toISOString().slice(0, 10);
  const yesterday = new Date(beijing.getTime() - 24 * 60 * 60 * 1000).toISOString().slice(0, 10);
  const tomorrow = new Date(beijing.getTime() + 24 * 60 * 60 * 1000).toISOString().slice(0, 10);
  return day === today || day === yesterday || day === tomorrow;
}

function json(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: jsonHeaders });
}
