// Publishes an AXE v2 release to the Cloudflare R2 update bucket (S3-compatible API, AWS Signature V4, no SDK).
//
//   node tools/release/publish-r2.mjs --credentials C:\Users\you\axe-production-secrets\r2-credentials.json \
//        --dir <output of sign-manifest.mjs> --version 2.0.3 [--no-latest]
//   node tools/release/publish-r2.mjs --credentials ... --put-file <file> --key test/x.json   (raw upload; test fixtures)
//
// The credentials file stays OUTSIDE the repository and is never shipped in AXE:
//   { "accountId": "...", "bucket": "axe-v2-updates", "accessKeyId": "...", "secretAccessKey": "...", "publicBaseUrl": "https://pub-xxxx.r2.dev" }
// Use an R2 API token scoped to "Object Read & Write" on this one bucket only.
//
// Layout (objects under releases/<version>/ are IMMUTABLE: this script refuses to overwrite them):
//   releases/<v>/AXE-v2-Setup-<v>.exe   the installer
//   releases/<v>/manifest.json          the signed manifest of that release (archive)
//   stable/latest.json                  the signed manifest AXE polls (the only mutable object; short cache)
// The pointer is written LAST, after the installer is confirmed readable through the public URL.

import { createHash, createHmac } from "node:crypto";
import { readFileSync } from "node:fs";
import { join, resolve } from "node:path";

const args = process.argv.slice(2);
const arg = (name) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const flag = (name) => args.includes(name);
const need = (name) => arg(name) ?? (console.error(`missing ${name}`), process.exit(1));

const creds = JSON.parse(readFileSync(resolve(need("--credentials")), "utf8"));
for (const field of ["accountId", "bucket", "accessKeyId", "secretAccessKey", "publicBaseUrl"]) {
  if (!creds[field]) { console.error(`credentials file lacks "${field}"`); process.exit(1); }
}
if (!creds.publicBaseUrl.startsWith("https://")) { console.error("publicBaseUrl must be https"); process.exit(1); }
const host = `${creds.accountId}.r2.cloudflarestorage.com`;
const sha256hex = (data) => createHash("sha256").update(data).digest("hex");
const hmac = (key, data) => createHmac("sha256", key).update(data).digest();

async function s3(method, key, body, headers = {}) {
  const payload = body ?? Buffer.alloc(0);
  const now = new Date();
  const amzDate = now.toISOString().replace(/[:-]|\.\d{3}/g, "");
  const day = amzDate.slice(0, 8);
  const path = `/${creds.bucket}/${key.split("/").map(encodeURIComponent).join("/")}`;
  const signedHeaders = { host, "x-amz-content-sha256": sha256hex(payload), "x-amz-date": amzDate, ...headers };
  const names = Object.keys(signedHeaders).map((n) => n.toLowerCase()).sort();
  const canonicalHeaders = names.map((n) => `${n}:${String(Object.entries(signedHeaders).find(([k]) => k.toLowerCase() === n)[1]).trim()}\n`).join("");
  const canonical = [method, path, "", canonicalHeaders, names.join(";"), signedHeaders["x-amz-content-sha256"]].join("\n");
  const scope = `${day}/auto/s3/aws4_request`;
  const toSign = ["AWS4-HMAC-SHA256", amzDate, scope, sha256hex(canonical)].join("\n");
  const kSigning = hmac(hmac(hmac(hmac(`AWS4${creds.secretAccessKey}`, day), "auto"), "s3"), "aws4_request");
  const signature = createHmac("sha256", kSigning).update(toSign).digest("hex");
  const authorization = `AWS4-HMAC-SHA256 Credential=${creds.accessKeyId}/${scope}, SignedHeaders=${names.join(";")}, Signature=${signature}`;
  const res = await fetch(`https://${host}${path}`, { method, headers: { ...signedHeaders, Authorization: authorization }, body: method === "PUT" ? payload : undefined });
  return res;
}

const exists = async (key) => (await s3("HEAD", key)).status === 200;

async function put(key, bytes, contentType, cacheControl, { immutable }) {
  if (immutable && (await exists(key))) { console.error(`REFUSED: ${key} already exists and releases are immutable.`); process.exit(2); }
  const res = await s3("PUT", key, bytes, { "content-type": contentType, "cache-control": cacheControl });
  if (!res.ok) { console.error(`upload of ${key} failed: HTTP ${res.status} ${(await res.text()).slice(0, 200)}`); process.exit(1); }
  console.log(`uploaded ${key} (${bytes.length} bytes)`);
}

async function verifyPublic(path, expectedSha256) {
  const res = await fetch(`${creds.publicBaseUrl.replace(/\/+$/, "")}/${path}`, { redirect: "manual", headers: { "cache-control": "no-cache" } });
  if (res.status !== 200) throw new Error(`public URL for ${path} answered ${res.status}`);
  const got = sha256hex(Buffer.from(await res.arrayBuffer()));
  if (got !== expectedSha256) throw new Error(`public copy of ${path} does not match what was uploaded`);
  console.log(`verified public ${path}`);
}

if (arg("--delete-key")) {
  // Only for test fixtures (test/ and unreleased 99.x.y versions): real releases are never deleted by this tool.
  const key = arg("--delete-key");
  if (!(key.startsWith("test/") || key.startsWith("releases/99."))) { console.error("REFUSED: only test fixtures can be deleted."); process.exit(2); }
  const res = await s3("DELETE", key);
  console.log(`deleted ${key}: HTTP ${res.status}`);
  process.exit(res.ok ? 0 : 1);
}

if (arg("--put-file")) {
  const bytes = readFileSync(resolve(arg("--put-file")));
  const key = need("--key");
  await put(key, bytes, key.endsWith(".exe") ? "application/octet-stream" : "text/plain; charset=utf-8", "no-cache", { immutable: false });
  process.exit(0);
}

const version = need("--version");
const dir = resolve(need("--dir"));
const installer = readFileSync(join(dir, `AXE-v2-Setup-${version}.exe`));
const manifest = readFileSync(join(dir, `manifest-${version}.json`));
const prefix = `releases/${version}`;

await put(`${prefix}/AXE-v2-Setup-${version}.exe`, installer, "application/octet-stream", "public, max-age=31536000, immutable", { immutable: true });
await put(`${prefix}/manifest.json`, manifest, "text/plain; charset=utf-8", "public, max-age=31536000, immutable", { immutable: true });
await verifyPublic(`${prefix}/AXE-v2-Setup-${version}.exe`, sha256hex(installer));
if (flag("--no-latest")) { console.log("--no-latest: the stable/latest.json pointer was NOT changed."); process.exit(0); }
await put("stable/latest.json", manifest, "text/plain; charset=utf-8", "no-cache, max-age=0", { immutable: false });
await verifyPublic("stable/latest.json", sha256hex(manifest));
console.log(`published ${version}; manifest: ${creds.publicBaseUrl.replace(/\/+$/, "")}/stable/latest.json`);
