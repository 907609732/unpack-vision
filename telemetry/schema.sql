CREATE TABLE IF NOT EXISTS daily_active (
    day TEXT NOT NULL,
    daily_id TEXT NOT NULL,
    platform TEXT NOT NULL,
    app_version TEXT NOT NULL,
    channel TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY(day, daily_id, platform)
);

CREATE INDEX IF NOT EXISTS ix_daily_active_day_platform
    ON daily_active(day, platform);

CREATE TABLE IF NOT EXISTS daily_totals (
    day TEXT NOT NULL,
    platform TEXT NOT NULL,
    active_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(day, platform)
);

CREATE TABLE IF NOT EXISTS daily_versions (
    day TEXT NOT NULL,
    platform TEXT NOT NULL,
    app_version TEXT NOT NULL,
    active_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(day, platform, app_version)
);
