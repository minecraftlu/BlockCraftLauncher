import { createHash } from "node:crypto";
import { pathToFileURL } from "node:url";

// Clumps is a real Minecraft Java project with small releases. The project's
// current distribution permission is checked each time this command is run.
const DEFAULT_PROJECT = "256717";
const SMALL_JAR = 1024 * 1024;

export async function smoke(origin, projectId = DEFAULT_PROJECT, fetcher = fetch) {
  const url = new URL(origin);
  if (url.protocol !== "https:" || url.username || url.password || url.port ||
      url.pathname !== "/" || url.search || url.hash || !/^[1-9]\d{0,11}$/.test(projectId))
    throw new Error("Provide the bare HTTPS service origin and an optional positive project ID.");

  async function get(path, cap) {
    const response = await fetcher(url.origin + path, { redirect: "error", signal: AbortSignal.timeout(45000) });
    if (!response.ok) {
      await response.body?.cancel();
      throw new Error(path + " failed with HTTP " + response.status);
    }
    const chunks = [];
    let size = 0;
    for await (const chunk of response.body) {
      size += chunk.byteLength;
      if (size > cap) throw new Error("Response exceeds the smoke test size limit.");
      chunks.push(chunk);
    }
    return Buffer.concat(chunks);
  }
  async function json(path) { return JSON.parse((await get(path, 12 * SMALL_JAR)).toString("utf8")); }
  if ((await json("/health")).configured !== true) throw new Error("Service is not configured.");
  const categories = (await json("/v1/categories?gameId=432&classId=6")).data;
  if (!Array.isArray(categories) || !categories.some(row => row.gameId === 432 && row.classId === 6))
    throw new Error("No Minecraft Java mod categories returned.");

  const projectPath = "/v1/mods/" + projectId;
  const project = (await json(projectPath)).data;
  if (project?.id !== Number(projectId) || project.gameId !== 432 || project.classId !== 6 ||
      project.isAvailable !== true || project.allowModDistribution !== true)
    throw new Error("This project is unavailable for third-party distribution.");
  const files = (await json(projectPath + "/files?pageSize=50")).data;
  const file = files.find(row => row.modId === project.id && row.gameId === 432 && row.isAvailable === true &&
    row.isServerPack !== true && /\.jar$/i.test(row.fileName) && !/-(dev|sources|javadoc)\.jar$/i.test(row.fileName) &&
    Number.isSafeInteger(row.id) && row.id > 0 && Number.isSafeInteger(row.fileLength) &&
    row.fileLength >= 4 && row.fileLength <= SMALL_JAR &&
    row.hashes?.some(hash => hash.algo === 1 && /^[a-f\d]{40}$/i.test(hash.value)));
  if (!file) throw new Error("No author-enabled JAR under 1 MiB found. Choose another project ID.");
  const expected = file.hashes.find(hash => hash.algo === 1 && /^[a-f\d]{40}$/i.test(hash.value)).value.toLowerCase();
  const bytes = await get(projectPath + "/files/" + file.id + "/download", file.fileLength);
  const sha1 = createHash("sha1").update(bytes).digest("hex");
  if (bytes.length !== file.fileLength || bytes.readUInt32BE(0) !== 0x504b0304 || sha1 !== expected)
    throw new Error("Downloaded JAR did not match the official file length, signature or SHA-1.");
  return { projectId: project.id, fileId: file.id, bytes: bytes.length, sha1 };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    if (!process.argv[2]) throw new Error("Usage: node smoke.mjs https://SERVICE-HOST [projectId]");
    console.log("PASS", await smoke(process.argv[2], process.argv[3]));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
