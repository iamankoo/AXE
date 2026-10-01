// Creates and uploads NEGATIVE test fixtures for the AXE v2 updater, to the live R2 bucket under test/ (manifests) and under
// unreleased versions 99.0.x (installers). They are never referenced by stable/latest.json. After the run, delete them with
// --cleanup. The client side of the test is `dotnet run --project v2/tools/AXEv2.EndToEnd -- update-negative`.
//
//   node tools/release/make-negative-fixtures.mjs --credentials <r2-credentials.json> --key <update-signing-private.jwk.json> \
//        --installer <a real AXE-v2-Setup-x.y.z.exe> [--cleanup]
//
// Cases (client expectations are in the harness): bad signature, tampered payload, malformed, unsigned JSON, http download URL,
// wrong product, downgrade, missing fields, other host, a MODIFIED installer with an unchanged manifest, a wrong sha256, a
// truncated installer and an oversized one.

import { createHash, generateKeyPairSync } from "node:crypto";
import { execFileSync } from "node:child_process";
import { mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { buildManifest, signManifest } from "./sign-manifest.mjs";

const args = process.argv.slice(2);
const arg = (n) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : undefined; };
const need = (n) => arg(n) ?? (console.error(`missing ${n}`), process.exit(1));
const here = dirname(fileURLToPath(import.meta.url));
const credentialsPath = resolve(need("--credentials"));
const credentials = JSON.parse(readFileSync(credentialsPath, "utf8"));
const base = credentials.publicBaseUrl.replace(/\/+$/, "");
const jwk = JSON.parse(readFileSync(resolve(need("--key")), "utf8"));
const installer = readFileSync(resolve(need("--installer")));
const sha = (b) => createHash("sha256").update(b).digest("hex");
const dir = join(process.env.TEMP ?? ".", "axe-negative-fixtures");

const run = (extra) => execFileSync("node", [join(here, "publish-r2.mjs"), "--credentials", credentialsPath, ...extra], { stdio: ["ignore", "pipe", "inherit"] });
const upload = (key, bytes) => { const f = join(dir, key.replace(/\//g, "__")); writeFileSync(f, bytes); run(["--put-file", f, "--key", key]); };

const common = { minimum: "2.0.0", baseUrl: base, date: "2026-10-01", notes: "negative test", mandatory: false };
const good = (version, overrides = {}, over = {}) => buildManifest({ ...common, version, sha256: sha(installer), size: installer.length, ...over, overrides });

const versions = ["99.0.1", "99.0.2", "99.0.3", "99.0.4"];
const manifests = {
  "bad-signature": signManifest(good("99.0.9"), generateKeyPairSync("ec", { namedCurve: "P-256" }).privateKey.export({ format: "jwk" })),
  "tampered-payload": (() => { const t = signManifest(good("99.0.9"), jwk); const [p, s] = t.split("."); return Buffer.from(Buffer.from(p, "base64url").toString().replace("99.0.9", "99.0.8")).toString("base64url") + "." + s; })(),
  "malformed": "<html>this is not a manifest</html>",
  "unsigned-json": JSON.stringify(good("99.0.9")),
  "http-url": signManifest(good("99.0.9", { downloadUrl: `${base.replace("https://", "http://")}/releases/99.0.9/AXE-v2-Setup-99.0.9.exe` }), jwk),
  "wrong-product": signManifest(good("99.0.9", { product: "axe-v1" }), jwk),
  "downgrade": signManifest(good("1.0.0", {}, { minimum: "1.0.0" }), jwk), // a perfectly valid, correctly signed OLD release
  "missing-fields": signManifest({ typ: "axe-update", product: "axe-v2", version: "99.0.9" }, jwk),
  "missing-sha256": signManifest(good("99.0.9", { sha256: undefined }), jwk),
  "other-host": signManifest(good("99.0.9", { downloadUrl: "https://example.com/releases/99.0.9/AXE-v2-Setup-99.0.9.exe" }), jwk),
  // download-level cases: valid, correctly signed manifests whose installer object is wrong in one way
  "tampered-installer": signManifest(good("99.0.1"), jwk),
  "wrong-sha256": signManifest(good("99.0.2", {}, { sha256: "0".repeat(64) }), jwk),
  "truncated-installer": signManifest(good("99.0.3"), jwk),
  "oversized-installer": signManifest(good("99.0.4"), jwk),
};

mkdirSync(dir, { recursive: true });
if (process.argv.includes("--cleanup")) {
  for (const name of Object.keys(manifests)) run(["--delete-key", `test/${name}.json`]);
  for (const v of versions) run(["--delete-key", `releases/${v}/AXE-v2-Setup-${v}.exe`]);
  run(["--delete-key", "test/ping.txt"]);
  rmSync(dir, { recursive: true, force: true });
  console.log("negative fixtures removed");
  process.exit(0);
}

const flipped = Buffer.from(installer); flipped[Math.floor(flipped.length / 2)] ^= 0xff;
upload("releases/99.0.1/AXE-v2-Setup-99.0.1.exe", flipped);                       // same size, one byte differs
upload("releases/99.0.2/AXE-v2-Setup-99.0.2.exe", installer);                     // genuine file, manifest hash is wrong
upload("releases/99.0.3/AXE-v2-Setup-99.0.3.exe", installer.subarray(0, Math.floor(installer.length / 2))); // truncated
upload("releases/99.0.4/AXE-v2-Setup-99.0.4.exe", Buffer.concat([installer, Buffer.alloc(4096)]));          // bigger than signed
for (const [name, text] of Object.entries(manifests)) upload(`test/${name}.json`, Buffer.from(text));
console.log(`uploaded ${Object.keys(manifests).length} manifests and ${versions.length} installers`);
