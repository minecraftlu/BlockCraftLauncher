import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { handle } from "./worker.mjs";
import { smoke } from "./smoke.mjs";

const mod = { id: 101, gameId: 432, classId: 6, isAvailable: true, allowModDistribution: true,
  latestFiles: [{ id: 202, downloadUrl: "https://edge.forgecdn.net/files/sensitive.jar" }] };
const file = { id: 202, modId: 101, gameId: 432, isAvailable: true, fileName: "sample.jar",
  fileLength: 4, hashes: [{ algo: 1, value: "f".repeat(40) }], downloadUrl: "https://other.invalid/malicious.jar" };
const api = "https://api.curseforge.com/v1";
const cdn = "https://edge.forgecdn.net/files/123/456/sample.jar";
const direct = "/v1/mods/101/files/202/download";
const env = { CURSEFORGE_API_KEY: "fixture-only", CLIENT_RATE_LIMIT: { limit: async () => ({ success: true }) } };
const wrap = data => Response.json({ data });
const request = (path, extra) => new Request("https://launcher.test" + path, extra);
const defaults = () => new Map([
  [api + "/mods/101", () => wrap(mod)],
  [api + "/mods/101/files/202", () => wrap(file)],
  [api + "/mods/101/files/202/download-url", () => wrap(cdn)],
  [cdn, () => new Response("test")]
]);

function fixture(routes = defaults()) {
  const calls = [];
  const fetcher = async (url, init) => {
    calls.push({ url, ...init });
    assert.equal(init.redirect, "manual");
    assert.equal(init.cache, "no-store");
    const handler = routes.get(url);
    if (!handler) throw new Error("Unexpected fixture request");
    return handler(init);
  };
  return { calls, call: (path, init, config = env) => handle(request(path, init), config, fetcher) };
}

test("verified project, file, and fresh download authorization precede the streamed JAR", async () => {
  const f = fixture();
  const response = await f.call(direct, { headers: { Authorization: "untrusted", Cookie: "untrusted",
    "x-api-key": "untrusted" } });
  assert.equal(response.status, 200);
  assert.equal(await response.text(), "test");
  assert.equal(response.headers.get("Cache-Control"), "no-store");
  assert.equal(response.headers.get("Content-Type"), "application/java-archive");
  assert.deepEqual(f.calls.map(c => c.url), [...defaults().keys()]);
  for (const c of f.calls) {
    assert.equal(c.headers["x-api-key"], env.CURSEFORGE_API_KEY);
    assert.equal(c.headers.Authorization, undefined);
    assert.equal(c.headers.Cookie, undefined);
  }
});

test("author opt-out, absent permissions, cross-game and mismatched records all block delivery", async () => {
  for (const project of [{ allowModDistribution: false }, { allowModDistribution: null },
    { allowModDistribution: undefined }, { id: 99 }, { gameId: 1 }, { classId: 4471 }, { isAvailable: false }]) {
    const routes = defaults();
    routes.set(api + "/mods/101", () => wrap({ ...mod, ...project }));
    const f = fixture(routes);
    assert.ok([403, 404].includes((await f.call(direct)).status));
    assert.equal(f.calls.length, 1);
  }
  for (const change of [{ id: 99 }, { modId: 99 }, { gameId: 1 }, { isAvailable: false },
    { isServerPack: true }, { fileLength: 2 ** 29 }, { hashes: [] }, { fileName: "x-dev.jar" }]) {
    const routes = defaults();
    routes.set(api + "/mods/101/files/202", () => wrap({ ...file, ...change }));
    const f = fixture(routes);
    assert.equal((await f.call(direct)).status, 404);
    assert.equal(f.calls.length, 2);
  }
});

test("CDN redirect is confined to approved origins and all redirect targets are keyless", async () => {
  const routes = defaults();
  const media = "https://mediafilez.forgecdn.net/files/123/456/sample.jar?q=1";
  routes.set(cdn, () => Response.redirect(media));
  routes.set(media, () => new Response("test"));
  const f = fixture(routes);
  assert.equal(await (await f.call(direct)).text(), "test");
  assert.equal(f.calls.at(-1).headers["x-api-key"], undefined);
  routes.set(api + "/mods/101/files/202/download-url", () => wrap(media));
  routes.set(media, () => Response.redirect(cdn));
  routes.set(cdn, () => new Response("test"));
  const reverse = fixture(routes);
  assert.equal(await (await reverse.call(direct)).text(), "test");
  assert.equal(reverse.calls.at(-2).headers["x-api-key"], undefined);
  assert.equal(reverse.calls.at(-1).headers["x-api-key"], undefined);
  routes.set(api + "/mods/101/files/202/download-url", () => wrap(cdn));
  for (const bad of ["https://edge.forgecdn.net.evil.test/files/a", "https://edge.forgecdn.net:8443/files/a",
    "http://edge.forgecdn.net/files/a", "https://user@edge.forgecdn.net/files/a", "https://evil.test/files/a"]) {
    routes.set(cdn, () => Response.redirect(bad));
    const x = fixture(routes);
    assert.equal((await x.call(direct)).status, 502);
    assert.equal(x.calls.length, 4);
    routes.set(api + "/mods/101/files/202/download-url", () => wrap(bad));
    const y = fixture(routes);
    assert.equal((await y.call(direct)).status, 502);
    assert.equal(y.calls.length, 3);
    routes.set(api + "/mods/101/files/202/download-url", () => wrap(cdn));
  }
});

test("absent or denied API-issued download URLs never fall back to the file record", async () => {
  const routes = defaults();
  routes.set(api + "/mods/101/files/202", () => wrap({ ...file, downloadUrl: cdn }));
  for (const response of [() => wrap(null), () => new Response("denied", { status: 403 })]) {
    routes.set(api + "/mods/101/files/202/download-url", response);
    const f = fixture(routes);
    assert.ok([403, 404].includes((await f.call(direct)).status));
    assert.equal(f.calls.length, 3);
  }
});

test("invalid upstream redirects, authorization, and quota errors never expose upstream bodies", async () => {
  for (const status of [301, 401, 403, 429, 500]) {
    const routes = defaults();
    routes.set(api + "/mods/101", () => new Response("private upstream diagnostic", {
      status, headers: { Location: "https://bad.invalid/", "Retry-After": "17" }
    }));
    const f = fixture(routes);
    const res = await f.call(direct);
    assert.equal(res.status, [301, 500].includes(status) ? 502 : status);
    assert.equal(res.headers.get("Cache-Control"), "no-store");
    assert.equal(res.headers.get("Location"), null);
    assert.doesNotMatch(await res.text(), /private|fixture-only/);
    if (status === 429) assert.equal(res.headers.get("Retry-After"), "17");
    assert.equal(f.calls.length, 1);
  }
});

test("list/search/category/detail/batch responses return only requested Minecraft mod data, without direct file URLs", async () => {
  const routes = defaults();
  const category = { id: 44, gameId: 432, classId: 6 };
  routes.set(api + "/categories?gameId=432&classId=6", () => wrap([category, { ...category, classId: 12 }]));
  routes.set(api + "/mods/search?gameId=432&classId=6&pageSize=20&searchFilter=abc",
    () => Response.json({ data: [mod, { ...mod, gameId: 7 }, { ...mod, allowModDistribution: false }],
      pagination: { totalCount: 20 } }));
  routes.set(api + "/mods/101/files?pageSize=50", () => wrap([file, { ...file, modId: 7 }]));
  routes.set(api + "/mods", init => {
    assert.equal(init.method, "POST");
    assert.deepEqual(JSON.parse(init.body), { modIds: [101] });
    return wrap([mod, { ...mod, id: 102 }]);
  });
  const f = fixture(routes);
  const search = await (await f.call("/v1/mods/search?gameId=432&classId=6&pageSize=20&searchFilter=abc")).json();
  assert.equal(search.data.length, 1);
  assert.equal(search.pagination.totalCount, 20);
  assert.equal(search.data[0].latestFiles[0].downloadUrl, undefined);
  assert.deepEqual((await (await f.call("/v1/categories")).json()).data, [category]);
  const files = await (await f.call("/v1/mods/101/files?pageSize=50")).json();
  assert.equal(files.data.length, 1);
  assert.equal(files.data[0].downloadUrl, undefined);
  assert.equal(files.data[0].hashes[0].value, file.hashes[0].value);
  assert.equal((await (await f.call("/v1/mods/101/files/202")).json()).data.modId, 101);
  assert.equal((await (await f.call("/v1/mods", { method: "POST", body: '{"modIds":[101,101]}' })).json()).data.length, 1);
  routes.set(api + "/mods/101", () => wrap({ ...mod, allowModDistribution: null }));
  for (const path of ["/v1/mods/101", "/v1/mods/101/files", "/v1/mods/101/files/202"])
    assert.equal((await f.call(path)).status, 403);
});

test("arbitrary paths, bad pagination, mixed game IDs and oversized bodies never reach upstream", async () => {
  const f = fixture();
  for (const path of ["/v1/mods/1/files/2/download-url", "/v1/mods/1?upstream=https://evil",
    "/v1/mods/search?gameId=5", "/v1/mods/search?classId=4471", "/v1/mods/search?index=9999&pageSize=50",
    "/v1/mods/search?categoryId=1&categoryId=2", "/v1/mods/search?__proto__=1", "/v1/mods/0/files"]) {
    assert.ok([400, 404].includes((await f.call(path)).status));
  }
  for (const body of ['{"modIds":[0]}', '{"modIds":[1],"url":"https://evil"}', "x".repeat(2049)])
    assert.equal((await f.call("/v1/mods", { method: "POST", body })).status, 400);
  assert.equal((await f.call(direct, { method: "POST" })).status, 405);
  assert.equal(f.calls.length, 0);
});

test("limiting uses the runtime client address only and fails closed without a configured service", async () => {
  const f = fixture();
  let key;
  const denied = { ...env, CLIENT_RATE_LIMIT: { async limit(input) { key = input.key; return { success: false }; } } };
  const response = await f.call(direct, { headers: { "CF-Connecting-IP": "192.0.2.1", "X-Forwarded-For": "fake" } }, denied);
  assert.equal(response.status, 429);
  assert.equal(response.headers.get("Retry-After"), "60");
  assert.equal(key, "192.0.2.1");
  assert.equal((await f.call(direct, {}, {})).status, 503);
  assert.equal(f.calls.length, 0);
});

test("health reports configuration without reading the provider or spending client quota", async () => {
  const f = fixture();
  const avoidQuota = { limit: async () => { throw new Error("unexpected quota check"); } };
  for (const config of [{ ...env, CLIENT_RATE_LIMIT: avoidQuota }, { CLIENT_RATE_LIMIT: avoidQuota }]) {
    const res = await f.call("/health", {}, config);
    assert.equal(res.status, config.CURSEFORGE_API_KEY ? 200 : 503);
    assert.deepEqual(await res.json(), { configured: Boolean(config.CURSEFORGE_API_KEY) });
    assert.equal(res.headers.get("Cache-Control"), "no-store");
  }
  assert.equal(f.calls.length, 0);
});

test("size checks reject changed, oversized and truncated byte streams", async () => {
  const routes = defaults();
  routes.set(cdn, () => new Response("toolong", { headers: { "Content-Length": "7" } }));
  assert.equal((await fixture(routes).call(direct)).status, 502);
  for (const bytes of ["toolong", "x"]) {
    routes.set(cdn, () => new Response(bytes));
    const result = await fixture(routes).call(direct);
    await assert.rejects(result.arrayBuffer());
  }
});

test("operator smoke checks health, categories, permissions and the actual gateway transfer", async () => {
  const routes = defaults();
  const bytes = Buffer.from([0x50, 0x4b, 0x03, 0x04]);
  const sha1 = createHash("sha1").update(bytes).digest("hex");
  const checkedFile = { ...file, hashes: [{ algo: 1, value: sha1 }] };
  routes.set(api + "/categories?gameId=432&classId=6", () => wrap([{ id: 44, gameId: 432, classId: 6 }]));
  routes.set(api + "/mods/101/files?pageSize=50", () => wrap([checkedFile]));
  routes.set(api + "/mods/101/files/202", () => wrap(checkedFile));
  routes.set(cdn, () => new Response(bytes));
  const f = fixture(routes);
  const client = (url, options) => {
    assert.equal(options.redirect, "error");
    return f.call(url.slice("https://launcher.test".length));
  };
  assert.deepEqual(await smoke("https://launcher.test", "101", client),
    { projectId: 101, fileId: 202, bytes: 4, sha1 });
  routes.set(cdn, () => new Response("oops"));
  await assert.rejects(smoke("https://launcher.test", "101", client), /did not match/);
});
