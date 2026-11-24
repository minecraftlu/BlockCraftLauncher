import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { DatabaseSync } from "node:sqlite";
import { createD1Limiter } from "./d1-rate-limit.mjs";
import { handle } from "./worker.mjs";

const migration = readFileSync(new URL("./migrations/0001_quota.sql", import.meta.url), "utf8");

function database(t) {
  const sqlite = new DatabaseSync(":memory:");
  t.after(() => sqlite.close());
  const clock = { seconds: 6000 };
  sqlite.function("unixepoch", () => BigInt(clock.seconds));
  sqlite.exec(migration);
  // Exercise the shipped schema and SQL together, with D1's atomic batch contract.
  function prepare(sql, values = []) {
    return {
      bind: (...args) => prepare(sql, args),
      run: () => ({ results: sqlite.prepare(sql).all(...values) })
    };
  }
  const db = {
    prepare,
    async batch(statements) {
      sqlite.exec("BEGIN");
      try {
        const result = statements.map(statement => statement.run());
        sqlite.exec("COMMIT");
        return result;
      } catch (error) {
        sqlite.exec("ROLLBACK");
        throw error;
      }
    }
  };
  return { sqlite, clock, db, limiter: createD1Limiter(db) };
}

test("parallel charges across adapter instances admit exactly 180 per fixed UTC minute", async t => {
  const { sqlite, clock, db, limiter } = database(t);
  const other = createD1Limiter(db);
  const client = { key: "192.0.2.45" };
  clock.seconds = 6059;
  const burst = await Promise.all(Array.from({ length: 190 }, (_, i) => (i % 2 ? limiter : other).limit(client)));
  assert.equal(burst.filter(result => result.success).length, 180);
  assert.deepEqual(await other.limit({ key: "2001:db8::90" }), { success: true });
  const rows = sqlite.prepare("SELECT bucket, window_minute, requests FROM curseforge_quota ORDER BY requests DESC").all();
  assert.equal(rows.length, 2);
  assert.deepEqual(rows.map(row => [row.window_minute, row.requests]), [[100, 180], [100, 1]]);
  for (const { bucket } of rows) assert.ok(Number.isInteger(bucket) && bucket >= 0 && bucket < 2 ** 20);
  clock.seconds = 6060;
  assert.deepEqual(await other.limit(client), { success: true });
  assert.deepEqual(sqlite.prepare("SELECT window_minute, requests FROM curseforge_quota").all()
    .map(row => [row.window_minute, row.requests]), [[101, 1]]);
});

test("only bounded numeric hashes are stored and expired buckets are pruned in bounded batches", async t => {
  const { sqlite, limiter } = database(t);
  assert.deepEqual(sqlite.prepare("PRAGMA table_info(curseforge_quota)").all().map(row => row.name),
    ["bucket", "window_minute", "requests"]);
  const seed = sqlite.prepare("INSERT INTO curseforge_quota VALUES (?, 0, 1)");
  for (let bucket = 0; bucket < 30; bucket++) seed.run(bucket);
  assert.throws(() => seed.run(2 ** 20), /CHECK/);
  assert.throws(() => seed.run(-1), /CHECK/);
  const stale = () => sqlite.prepare("SELECT count(*) AS n FROM curseforge_quota WHERE window_minute = 0").get().n;
  for (const expected of [22, 14, 6, 0]) {
    assert.deepEqual(await limiter.limit({ key: "2001:db8::900" }), { success: true });
    assert.equal(stale(), expected);
  }
  assert.equal(sqlite.prepare("SELECT count(*) AS n FROM curseforge_quota").get().n, 1);
});

test("invalid identities do not create buckets and broken D1 never admits upstream traffic", async t => {
  const { sqlite, limiter, db } = database(t);
  for (const key of ["", "x".repeat(129), null])
    assert.deepEqual(await limiter.limit({ key }), { success: false });
  assert.equal(sqlite.prepare("SELECT count(*) AS n FROM curseforge_quota").get().n, 0);
  let upstream = 0;
  const req = new Request("https://launcher.test/v1/categories", { headers: { "CF-Connecting-IP": "192.0.2.5" } });
  const env = { CURSEFORGE_API_KEY: "fixture", CURSEFORGE_QUOTA: db };
  const mock = () => { upstream++; return Response.json({ data: [] }); };
  assert.equal((await handle(req, env, mock)).status, 200);
  assert.equal(upstream, 1);
  for (let i = 1; i < 180; i++) await limiter.limit({ key: "192.0.2.5" });
  const limited = await handle(req, env, mock);
  assert.equal(limited.status, 429);
  assert.equal(limited.headers.get("Retry-After"), "60");
  assert.equal(upstream, 1);
  sqlite.exec("DROP TABLE curseforge_quota");
  assert.ok((await handle(req, env, mock)).status >= 500);
  assert.equal(upstream, 1);
});
