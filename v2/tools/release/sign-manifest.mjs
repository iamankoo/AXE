// Builds and signs an AXE v2 update manifest for an installer.
//
//   node tools/release/sign-manifest.mjs --key <update-signing-private.jwk.json> --installer <AXE-v2-Setup.exe> \
//        --version 2.0.3 --base-url https://pub-xxxx.r2.dev --out <dir> --notes "What changed" \
//        [--minimum 2.0.0] [--mandatory] [--date 2026-10-01]
//
// Writes <dir>/AXE-v2-Setup-<version>.exe (a copy of the installer under its published name) and
// <dir>/manifest-<version>.json (the signed manifest text). The manifest is the same signed-message format the apps use for
// grants: base64url(JSON payload) + "." + base64url(ECDSA P-256/SHA-256 signature, IEEE P1363) over the payload TEXT.
// It never prints the private key. The installer's SHA-256 and size are computed here from the actual file.

import { createHash, createPrivateKey, sign } from "node:crypto";
import { copyFileSync, mkdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

const args = process.argv.slice(2);
const arg = (name) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const flag = (name) => args.includes(name);
const need = (name) => arg(name) ?? (console.error(`missing ${name}`), process.exit(1));

export function buildManifest({ version, minimum, baseUrl, sha256, size, date, notes, mandatory, overrides = {} }) {
  const root = baseUrl.replace(/\/+$/, "");
  return {
    typ: "axe-update",
    product: "axe-v2",
    version,
    minimumSupportedVersion: minimum,
    downloadUrl: `${root}/releases/${version}/AXE-v2-Setup-${version}.exe`,
    sha256,
    size,
    releaseDate: date,
    releaseNotes: notes,
    mandatory,
    ...overrides,
  };
}

export function signManifest(manifest, privateJwk) {
  const payload = Buffer.from(JSON.stringify(manifest), "utf8").toString("base64url");
  const key = createPrivateKey({ key: privateJwk, format: "jwk" });
  const signature = sign("sha256", Buffer.from(payload, "ascii"), { key, dsaEncoding: "ieee-p1363" });
  return `${payload}.${signature.toString("base64url")}`;
}

if (import.meta.url === `file://${process.argv[1].replace(/\\/g, "/")}` || process.argv[1]?.endsWith("sign-manifest.mjs")) {
  const version = need("--version");
  if (!/^\d{1,4}\.\d{1,4}\.\d{1,4}$/.test(version)) { console.error("version must look like 2.0.3"); process.exit(1); }
  const baseUrl = need("--base-url");
  if (!baseUrl.startsWith("https://")) { console.error("--base-url must be https"); process.exit(1); }
  const installer = resolve(need("--installer"));
  const out = resolve(need("--out"));
  const bytes = readFileSync(installer);
  const manifest = buildManifest({
    version,
    minimum: arg("--minimum") ?? "2.0.0",
    baseUrl,
    sha256: createHash("sha256").update(bytes).digest("hex"),
    size: statSync(installer).size,
    date: arg("--date") ?? new Date().toISOString().slice(0, 10),
    notes: need("--notes"),
    mandatory: flag("--mandatory"),
  });
  mkdirSync(out, { recursive: true });
  copyFileSync(installer, join(out, `AXE-v2-Setup-${version}.exe`));
  const text = signManifest(manifest, JSON.parse(readFileSync(need("--key"), "utf8")));
  writeFileSync(join(out, `manifest-${version}.json`), text);
  console.log(JSON.stringify({ version, sha256: manifest.sha256, size: manifest.size, downloadUrl: manifest.downloadUrl, manifestSha256: createHash("sha256").update(text).digest("hex"), signature: text.slice(text.indexOf(".") + 1, text.indexOf(".") + 17) + "..." }, null, 2));
}
