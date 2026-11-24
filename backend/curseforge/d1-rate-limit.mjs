const consume = `
  INSERT INTO curseforge_quota (bucket, window_minute, requests)
  VALUES (?, unixepoch() / 60, 1)
  ON CONFLICT(bucket) DO UPDATE SET
    window_minute = excluded.window_minute,
    requests = CASE WHEN excluded.window_minute > window_minute THEN 1 ELSE requests + 1 END
  WHERE excluded.window_minute > window_minute
     OR (excluded.window_minute = window_minute AND requests < 180)
  RETURNING requests
`;

const prune = `
  DELETE FROM curseforge_quota WHERE bucket IN (
    SELECT bucket FROM curseforge_quota
    WHERE window_minute < unixepoch() / 60
    ORDER BY window_minute LIMIT 8
  )
`;

export function createD1Limiter(db) {
  return {
    async limit({ key }) {
      if (typeof key !== "string" || !key.length || key.length > 128)
        return { success: false };
      const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(key));
      // A fixed 20-bit keyspace bounds storage. Colliding clients share their allowance.
      const bucket = new DataView(digest).getUint32(0) >>> 12;
      const [, charge] = await db.batch([db.prepare(prune), db.prepare(consume).bind(bucket)]);
      return { success: charge.results.length === 1 };
    }
  };
}
