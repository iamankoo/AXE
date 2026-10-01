// Test helper: every simulated AXE installation gets its OWN ECDSA P-256 key pair, exactly like the real Windows app
// (device id = SHA-256 of the public key's SPKI DER). Not a test file (no tests here).

import { createHash, generateKeyPairSync, sign } from "node:crypto";

const registry = new Map(); // device id -> { hash, publicKey (base64 SPKI), privateKey }

/** A fresh installation identity. */
export function newDevice() {
  const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
  const der = publicKey.export({ type: "spki", format: "der" });
  const hash = createHash("sha256").update(der).digest("hex");
  const device = { hash, publicKey: der.toString("base64"), privateKey };
  registry.set(hash, device);
  return device;
}

/** Legacy convenience for tests that only need the device id string (its key is registered for automatic use). */
export const device = () => newDevice().hash;

export const deviceById = (hash) => registry.get(hash);

/** The proof a device signs for one authorization check (same message as the Windows client). */
export function proofFor(dev, nonce, grantId) {
  return sign("sha256", Buffer.from(`axe-device-proof|${nonce}|${grantId}`), { key: dev.privateKey, dsaEncoding: "ieee-p1363" }).toString("base64url");
}

/** The claims inside a grant token (unverified: for tests to read `jti` and `did`). */
export function claimsOf(grant) {
  try { return JSON.parse(Buffer.from(String(grant).split(".")[0], "base64url").toString()); } catch { return {}; }
}

/** Proof by the installation named in the grant itself (`did`), if this test run created that installation. */
export function proofForGrant(grant, nonce) {
  const claims = claimsOf(grant);
  const dev = registry.get(claims.did);
  return dev && claims.jti ? proofFor(dev, nonce, claims.jti) : undefined;
}

/** Adds the installation's public key to a submission body, as the real client does, unless the test set one itself. */
export function withDeviceKey(url, method, body) {
  if (method === "POST" && String(url).endsWith("/access/requests") && body && typeof body.device === "string" && body.devicePublicKey === undefined) {
    const dev = registry.get(body.device);
    if (dev) return { ...body, devicePublicKey: dev.publicKey };
  }
  return body;
}
