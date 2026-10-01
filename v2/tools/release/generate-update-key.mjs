// Generates the RELEASE (update-signing) key pair for AXE v2, into a directory OUTSIDE any git repository.
//
//   node tools/release/generate-update-key.mjs --out C:\Users\you\axe-production-secrets
//
// Writes:
//   update-signing-private.jwk.json   ECDSA P-256 private key (SECRET: signs update manifests; never commit, never ship)
//   update-public.json                { "releasePublicKey": "<base64 SubjectPublicKeyInfo>" }  (public; goes into server.json)
//
// This key is separate from the server's grant-signing key. If the private key is lost, no update can be published that
// existing installations will trust: they would have to be reinstalled with a build that carries a new public key.
// The script refuses to write inside a git repository or to overwrite an existing key.

import { generateKeyPairSync } from "node:crypto";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";

const i = process.argv.indexOf("--out");
if (i < 0 || !process.argv[i + 1]) {
  console.error("usage: node tools/release/generate-update-key.mjs --out <directory outside any git repository>");
  process.exit(1);
}
const out = resolve(process.argv[i + 1]);

for (let dir = out; ; dir = dirname(dir)) {
  if (existsSync(join(dir, ".git"))) {
    console.error(`refusing to write a private key inside a git repository (${dir}).`);
    process.exit(1);
  }
  if (dirname(dir) === dir) break;
}

const privatePath = join(out, "update-signing-private.jwk.json");
if (existsSync(privatePath)) {
  console.error("an update-signing key already exists there; refusing to overwrite it.");
  process.exit(1);
}

mkdirSync(out, { recursive: true });
const { publicKey, privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
writeFileSync(privatePath, JSON.stringify(privateKey.export({ format: "jwk" })), { mode: 0o600 });
writeFileSync(join(out, "update-public.json"), JSON.stringify({ releasePublicKey: publicKey.export({ type: "spki", format: "der" }).toString("base64") }));
console.log(`Wrote update-signing-private.jwk.json (secret) and update-public.json (public) to ${out}`);
