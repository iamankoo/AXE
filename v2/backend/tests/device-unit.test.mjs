// Unit tests of the device-identity module (supabase/functions/_shared/device.ts), plain Node:
//   node --test tests/device-unit.test.mjs

import { test } from "node:test";
import assert from "node:assert/strict";
import { createHash, generateKeyPairSync, sign } from "node:crypto";
import { parseDevicePublicKey, proofMessage, verifyDeviceProof } from "../supabase/functions/_shared/device.ts";
import { newDevice, proofFor } from "./device-helper.mjs";

const spkiOf = (publicKey) => publicKey.export({ type: "spki", format: "der" });
const hashOf = (der) => createHash("sha256").update(der).digest("hex");

test("a genuine installation key is accepted and normalised", async () => {
  const d = newDevice();
  assert.equal(await parseDevicePublicKey(d.publicKey, d.hash), d.publicKey);
});

test("the key must hash to the claimed device id (an id cannot be chosen or borrowed)", async () => {
  const a = newDevice();
  const b = newDevice();
  assert.equal(await parseDevicePublicKey(a.publicKey, b.hash), null, "A's key under B's id");
  assert.equal(await parseDevicePublicKey(b.publicKey, a.hash), null, "B's key under A's id");
  assert.equal(await parseDevicePublicKey(a.publicKey, "0".repeat(64)), null);
  assert.equal(await parseDevicePublicKey(a.publicKey, a.hash.toUpperCase()), null, "ids are lowercase hex");
});

test("only valid P-256 public keys are accepted", async () => {
  const p384 = spkiOf(generateKeyPairSync("ec", { namedCurve: "P-384" }).publicKey);
  const rsa = spkiOf(generateKeyPairSync("rsa", { modulusLength: 2048 }).publicKey);
  assert.equal(await parseDevicePublicKey(p384.toString("base64"), hashOf(p384)), null, "P-384");
  assert.equal(await parseDevicePublicKey(rsa.toString("base64"), hashOf(rsa)), null, "RSA (also too long)");
  const d = newDevice();
  const der = Buffer.from(d.publicKey, "base64");
  for (const bad of [undefined, null, 42, "", "short", "!!!not base64!!!" + "A".repeat(100), "A".repeat(300), d.publicKey.slice(0, -4),
    Buffer.concat([der.subarray(0, 40), Buffer.alloc(der.length - 40)]).toString("base64")]) {
    assert.equal(await parseDevicePublicKey(bad, d.hash), null, String(bad).slice(0, 30));
  }
});

test("a proof is valid only for the exact key, nonce and grant it was made for", async () => {
  const d = newDevice();
  const other = newDevice();
  const nonce = "N".repeat(24);
  const jti = crypto.randomUUID();
  const proof = proofFor(d, nonce, jti);

  assert.equal(await verifyDeviceProof(d.publicKey, proof, nonce, jti), true);
  assert.equal(await verifyDeviceProof(other.publicKey, proof, nonce, jti), false, "another installation's key");
  assert.equal(await verifyDeviceProof(d.publicKey, proof, "M".repeat(24), jti), false, "replayed for a different nonce");
  assert.equal(await verifyDeviceProof(d.publicKey, proof, nonce, crypto.randomUUID()), false, "for a different grant");
  assert.equal(await verifyDeviceProof(d.publicKey, proofFor(other, nonce, jti), nonce, jti), false, "signed by someone else");
});

test("malformed proofs are refused, never thrown", async () => {
  const d = newDevice();
  const nonce = "N".repeat(24);
  const jti = crypto.randomUUID();
  const good = proofFor(d, nonce, jti);
  for (const bad of [undefined, null, 7, {}, "", "!!!", "A", good.slice(0, -2), good + "AA", "A".repeat(86), "A".repeat(500), good.replace(/./, "*")]) {
    assert.equal(await verifyDeviceProof(d.publicKey, bad, nonce, jti), false, String(bad).slice(0, 20));
  }
  assert.equal(await verifyDeviceProof("not a key", good, nonce, jti), false, "a broken stored key");
});

test("the signed message format is fixed (the Windows client signs exactly this)", () => {
  assert.equal(proofMessage("NONCE", "JTI"), "axe-device-proof|NONCE|JTI");
  // DER signatures (not IEEE P1363) must not verify: the client and server agree on the 64-byte fixed form.
  const d = newDevice();
  const der = sign("sha256", Buffer.from(proofMessage("n", "j")), d.privateKey).toString("base64url");
  return verifyDeviceProof(d.publicKey, der, "n", "j").then((ok) => assert.equal(ok, false));
});
