// Generates the PRODUCTION cryptographic material for AXE v2, into a directory OUTSIDE the repository.
//
//   node scripts/generate-production-keys.mjs --out D:\secure\axe-production
//
// Writes (and prints nothing secret to the console):
//   signing-private.jwk.json   the server's ECDSA P-256 signing key  -> backend secret AXE_SIGNING_PRIVATE_JWK  (SECRET)
//   hash-secret.txt            a random 32-byte secret               -> backend secret AXE_HASH_SECRET          (SECRET)
//   client-public.json         the PUBLIC verification key           -> goes into the Windows/Android config    (public)
//
// Back the files up somewhere safe (a password manager / offline vault). If signing-private.jwk.json is lost or replaced,
// every issued grant stops validating and the public key embedded in AXE must be rebuilt and redistributed.
// It refuses to write inside a git repository.

import { generateKeyPairSync, randomBytes } from "node:crypto";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";

const i = process.argv.indexOf("--out");
if (i < 0 || !process.argv[i + 1]) {
  console.error("usage: node scripts/generate-production-keys.mjs --out <directory outside any git repository>");
  process.exit(1);
}
const out = resolve(process.argv[i + 1]);

for (let dir = out; ; dir = dirname(dir)) {
  if (existsSync(join(dir, ".git"))) {
    console.error(`refusing to write secrets inside a git repository (${dir}). Choose a directory outside it.`);
    process.exit(1);
  }
  if (dirname(dir) === dir) break;
}
for (const name of ["signing-private.jwk.json", "hash-secret.txt"]) {
  if (existsSync(join(out, name))) {
    console.error(`${name} already exists in ${out}; refusing to overwrite production keys.`);
    process.exit(1);
  }
}

mkdirSync(out, { recursive: true });
const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const jwk = JSON.stringify(privateKey.export({ format: "jwk" }));
const spki = publicKey.export({ type: "spki", format: "der" }).toString("base64");

writeFileSync(join(out, "signing-private.jwk.json"), jwk + "\n", { mode: 0o600 });
writeFileSync(join(out, "hash-secret.txt"), randomBytes(32).toString("base64url") + "\n", { mode: 0o600 });
writeFileSync(join(out, "client-public.json"), JSON.stringify({ signingPublicKey: spki }, null, 2) + "\n");

console.log(`Wrote 3 files to ${out}`);
console.log("Next: supabase secrets set AXE_SIGNING_PRIVATE_JWK=\"$(cat signing-private.jwk.json)\" AXE_HASH_SECRET=\"$(cat hash-secret.txt)\"");
console.log("The public key (client-public.json) goes into the production server.json; see v2/PRODUCTION.md.");
