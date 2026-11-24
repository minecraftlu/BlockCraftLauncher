CREATE TABLE IF NOT EXISTS curseforge_quota (
    bucket INTEGER PRIMARY KEY CHECK (bucket BETWEEN 0 AND 1048575),
    window_minute INTEGER NOT NULL CHECK (window_minute >= 0),
    requests INTEGER NOT NULL CHECK (requests BETWEEN 1 AND 180)
) STRICT;

CREATE INDEX IF NOT EXISTS curseforge_quota_window ON curseforge_quota(window_minute);
