import { createD1Limiter } from "./d1-rate-limit.mjs";

const API = "https://api.curseforge.com";
const EDGE = "https://edge.forgecdn.net";
const CDN_ORIGINS = new Set([EDGE, "https://mediafilez.forgecdn.net", "https://media.forgecdn.net"]);
const METADATA_LIMIT = 12 * 1024 * 1024;
const FILE_LIMIT = 200 * 1024 * 1024;
const PRIVATE = { "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" };
const JSON_HEADERS = { ...PRIVATE, "Content-Type": "application/json; charset=utf-8" };
const MINECRAFT = 432, MODS = 6;
const positiveId = value => Number.isSafeInteger(value) && value > 0 && value <= 999999999999;

class ServiceError extends Error {
  constructor(status, message, retryAfter) {
    super(message);
    this.status = status;
    this.retryAfter = retryAfter;
  }
}

const badInput = () => new ServiceError(400, "Invalid request.");
const unavailable = () => new ServiceError(404, "Project or file unavailable.");
const badUpstream = () => new ServiceError(502, "Invalid response from the official service.");

async function rejectUpstream(response) {
  await response.body?.cancel();
  const status = response.status;
  if (status === 401 || status === 403) throw new ServiceError(status, "Official service denied access.");
  if (status === 404) throw unavailable();
  if (status === 429) {
    const seconds = Number(response.headers.get("Retry-After"));
    throw new ServiceError(429, "Official service rate limit.", seconds > 0 ? Math.min(3600, Math.ceil(seconds)) : 60);
  }
  throw badUpstream();
}

// Enforce the cap even without a Content-Length header. Limit data before decoding.
async function readJson(message, cap, problem) {
  if (!message.body || Number(message.headers.get("Content-Length")) > cap) {
    await message.body?.cancel();
    throw problem();
  }
  const reader = message.body.getReader();
  let length = 0, text = "";
  const decoder = new TextDecoder();
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    length += value.byteLength;
    if (length > cap) {
      await reader.cancel();
      throw problem();
    }
    text += decoder.decode(value, { stream: true });
  }
  try { return JSON.parse(text + decoder.decode()); } catch { throw problem(); }
}

const commonFilters = {
  index: value => /^\d{1,4}$/.test(value),
  pageSize: value => /^[1-9]\d?$/.test(value) && Number(value) <= 50,
  gameVersion: value => /^[a-zA-Z0-9._-]{1,64}$/.test(value),
  modLoaderType: value => /^[1-6]$/.test(value)
};
const gameFilters = { gameId: value => value === "432", classId: value => value === "6" };
const searchFilters = {
  ...gameFilters, ...commonFilters,
  categoryId: value => /^[1-9]\d{0,11}$/.test(value),
  searchFilter: value => value.length <= 150 && !/[\u0000-\u001f\u007f]/.test(value),
  sortField: value => /^(1|2|3|6|11)$/.test(value),
  sortOrder: value => value === "desc" || value === "asc"
};

function queryFor(url, validators, minecraftOnly = false) {
  const query = new URLSearchParams();
  for (const [key, value] of url.searchParams) {
    if (query.has(key) || !Object.hasOwn(validators, key) || !validators[key](value)) throw badInput();
    query.set(key, value);
  }
  if (minecraftOnly) {
    query.set("gameId", String(MINECRAFT));
    query.set("classId", String(MODS));
  }
  if (Number(query.get("index")) + Number(query.get("pageSize") || 50) > 10000) throw badInput();
  return query.size ? "?" + query.toString() : "";
}

const javaMod = project => positiveId(project?.id) && project.gameId === MINECRAFT &&
  project.classId === MODS && project.isAvailable === true;
const projectFile = (file, projectId) => positiveId(file?.id) && file.modId === projectId &&
  file.gameId === MINECRAFT && file.isAvailable === true;

function jsonResult(envelope) {
  // Official metadata can contain direct CDN URLs in both files and nested
  // latestFiles. Desktop clients obtain the binary through the checked route.
  return new Response(JSON.stringify(envelope, (key, value) => key === "downloadUrl" ? undefined : value),
    { headers: JSON_HEADERS });
}

function cdnUrl(value, base) {
  let url;
  try { url = new URL(value, base); } catch { throw badUpstream(); }
  if (!CDN_ORIGINS.has(url.origin) || url.username || url.password || url.hash ||
      !url.pathname.startsWith("/files/")) throw badUpstream();
  return url;
}

async function streamFile(download, file, key, signal, fetcher) {
  if (typeof download !== "string" || !download) throw unavailable();
  let target = cdnUrl(download);
  for (let hop = 0; hop < 5; hop++) {
    const headers = { "Accept-Encoding": "identity" };
    // Rebuild headers on each hop. Only an edge URL issued directly by the
    // authenticated API can receive the key. Redirect targets never receive it.
    if (hop === 0 && target.origin === EDGE) headers["x-api-key"] = key;
    const response = await fetcher(target.href, { headers, redirect: "manual", cache: "no-store", signal });
    if ([301, 302, 303, 307, 308].includes(response.status)) {
      const location = response.headers.get("Location");
      await response.body?.cancel();
      if (!location) throw badUpstream();
      target = cdnUrl(location, target);
      continue;
    }
    if (response.status !== 200) await rejectUpstream(response);
    const length = response.headers.get("Content-Length");
    if (!response.body || (length !== null && Number(length) !== file.fileLength)) {
      await response.body?.cancel();
      throw badUpstream();
    }
    const reader = response.body.getReader();
    let seen = 0;
    const checked = new ReadableStream({
      async pull(controller) {
        const { value, done } = await reader.read();
        if (done) {
          if (seen !== file.fileLength) throw badUpstream();
          controller.close();
          return;
        }
        seen += value.byteLength;
        if (seen > file.fileLength) {
          await reader.cancel();
          throw badUpstream();
        }
        controller.enqueue(value);
      },
      cancel(reason) { return reader.cancel(reason); }
    });
    return new Response(checked, { headers: {
      ...PRIVATE, "Content-Type": "application/java-archive", "Content-Disposition": "attachment",
      "Content-Length": String(file.fileLength)
    } });
  }
  throw badUpstream();
}

async function route(request, env, fetcher) {
  const url = new URL(request.url);
  if (url.protocol !== "https:" || url.search.length > 2000) throw badInput();
  const limiter = env.CLIENT_RATE_LIMIT || (env.CURSEFORGE_QUOTA && createD1Limiter(env.CURSEFORGE_QUOTA));
  const configured = Boolean(env.CURSEFORGE_API_KEY && limiter);
  if (url.pathname === "/health" && request.method === "GET") {
    queryFor(url, {});
    return new Response(JSON.stringify({ configured }), { status: configured ? 200 : 503, headers: JSON_HEADERS });
  }
  const bulk = url.pathname === "/v1/mods";
  if (request.method !== (bulk ? "POST" : "GET")) throw new ServiceError(405, "Method unavailable.");
  if (!configured) throw new ServiceError(503, "Service not configured.");
  // Cloudflare supplies the client address. Never trust caller-supplied forwarding headers.
  const client = request.headers.get("CF-Connecting-IP") || "unknown";
  if (!(await limiter.limit({ key: client })).success)
    throw new ServiceError(429, "Too many requests.", 60);

  async function api(path, body) {
    const headers = { Accept: "application/json", "x-api-key": env.CURSEFORGE_API_KEY };
    if (body) headers["Content-Type"] = "application/json";
    const response = await fetcher(API + "/v1" + path, {
      method: body ? "POST" : "GET", body, headers, redirect: "manual", cache: "no-store", signal: request.signal
    });
    if (response.status !== 200) await rejectUpstream(response);
    const envelope = await readJson(response, METADATA_LIMIT, badUpstream);
    if (!envelope || !Object.hasOwn(envelope, "data")) throw badUpstream();
    return envelope;
  }

  function filtered(envelope, accept) {
    if (!Array.isArray(envelope.data)) throw badUpstream();
    return jsonResult({ ...envelope, data: envelope.data.filter(accept) });
  }

  if (bulk) {
    queryFor(url, {});
    const input = await readJson(request, 2048, badInput);
    const ids = input?.modIds;
    if (Object.keys(input || {}).length !== 1 || !Array.isArray(ids) || !ids.length ||
        ids.length > 32 || !ids.every(positiveId)) throw badInput();
    const unique = new Set(ids);
    const results = await api("/mods", JSON.stringify({ modIds: [...unique] }));
    return filtered(results, project => javaMod(project) && project.allowModDistribution === true && unique.has(project.id));
  }
  if (url.pathname === "/v1/categories") {
    const rows = await api("/categories" + queryFor(url, gameFilters, true));
    return filtered(rows, category => category?.gameId === MINECRAFT && category.classId === MODS);
  }
  if (url.pathname === "/v1/mods/search") {
    const rows = await api("/mods/search" + queryFor(url, searchFilters, true));
    return filtered(rows, project => javaMod(project) && project.allowModDistribution === true);
  }
  const match = /^\/v1\/mods\/([1-9]\d{0,11})(?:\/(files)(?:\/([1-9]\d{0,11})(?:\/(download))?)?)?$/.exec(url.pathname);
  if (!match) throw unavailable();
  const [, projectId, files, fileId, download] = match;
  const fileQuery = queryFor(url, files && !fileId ? commonFilters : {});
  const projectPath = "/mods/" + projectId;
  const project = await api(projectPath);
  if (!javaMod(project.data) || project.data.id !== Number(projectId)) throw unavailable();
  if (project.data.allowModDistribution !== true)
    throw new ServiceError(403, "Third-party distribution is unavailable.");
  if (!files) return jsonResult(project);
  if (!fileId) {
    return filtered(await api(projectPath + "/files" + fileQuery), file => projectFile(file, Number(projectId)));
  }
  const filePath = projectPath + "/files/" + fileId;
  const result = await api(filePath);
  const file = result.data;
  if (!projectFile(file, Number(projectId)) || file.id !== Number(fileId)) throw unavailable();
  if (!download) return jsonResult(result);
  if (file.isServerPack === true || typeof file.fileName !== "string" || file.fileName.length > 180 ||
      !/^[^<>:"/\\|?*\u0000-\u001f]+\.jar$/i.test(file.fileName) || /-(sources|dev|javadoc)\.jar$/i.test(file.fileName) ||
      !Number.isSafeInteger(file.fileLength) || file.fileLength < 1 || file.fileLength > FILE_LIMIT ||
      !Array.isArray(file.hashes) || !file.hashes.some(hash => hash?.algo === 1 && /^[a-f\d]{40}$/i.test(hash.value)))
    throw unavailable();
  const resolved = await api(filePath + "/download-url");
  return streamFile(resolved.data, file, env.CURSEFORGE_API_KEY, request.signal, fetcher);
}

export async function handle(request, env, fetcher = fetch) {
  try { return await route(request, env, fetcher); }
  catch (error) {
    const publicError = error instanceof ServiceError ? error : badUpstream();
    const headers = { ...JSON_HEADERS };
    if (publicError.retryAfter) headers["Retry-After"] = String(publicError.retryAfter);
    return new Response(JSON.stringify({ error: publicError.message }), { status: publicError.status, headers });
  }
}

export default { fetch: (request, env) => handle(request, env) };
