// Device identity for AXE v2 installations (WebCrypto only; no Deno APIs, so Node can unit-test it too).
//
// Every installation generates its OWN ECDSA P-256 key pair on first run; the private key never leaves the PC (Windows DPAPI).
// The device id ("device hash") is the lowercase hex SHA-256 of the public key's SubjectPublicKeyInfo DER, so:
//   * an id cannot be chosen freely or shared: it is derived from a key only that installation holds;
//   * the server registers the PUBLIC key with the request, checks it matches the id, and copies it onto the grant;
//   * every authorization check (POST /access/session) must carry a fresh signature made with that private key, bound to the
//     request nonce and the grant id. A copied grant (even with its device id, which is public inside the grant) is useless
//     on another installation, because that installation cannot produce the signature.

const encoder = new TextEncoder();

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

function fromBase64(value: string): Uint8Array {
  if (!/^[A-Za-z0-9+/]+={0,2}$/.test(value)) throw new Error("bad base64");
  return Uint8Array.from(atob(value), (c) => c.charCodeAt(0));
}

function fromBase64Url(value: string): Uint8Array {
  if (!/^[A-Za-z0-9_-]+$/.test(value)) throw new Error("bad base64url");
  const s = value.replace(/-/g, "+").replace(/_/g, "/");
  return Uint8Array.from(atob(s + "===".slice((s.length + 3) % 4)), (c) => c.charCodeAt(0));
}

/** The exact message a device signs to prove possession of its key for one authorization check. */
export const proofMessage = (nonce: string, grantId: string): string => `axe-device-proof|${nonce}|${grantId}`;

const keyAlgorithm = { name: "ECDSA", namedCurve: "P-256" } as const;

/**
 * Validates a device's registered public key: well-formed base64 SPKI, a real P-256 key, and its SHA-256 equals the claimed
 * device id. Returns the normalised base64 key to store, or null if anything is wrong.
 */
export async function parseDevicePublicKey(value: unknown, deviceId: string): Promise<string | null> {
  if (typeof value !== "string" || value.length < 80 || value.length > 200) return null;
  try {
    const der = fromBase64(value);
    await crypto.subtle.importKey("spki", der, keyAlgorithm, false, ["verify"]); // rejects anything that is not a valid P-256 key
    const hash = toHex(new Uint8Array(await crypto.subtle.digest("SHA-256", der)));
    return hash === deviceId ? value : null;
  } catch {
    return null;
  }
}

/** True only if `proof` is a valid ECDSA P-256 / SHA-256 (IEEE P1363) signature by the stored device key over this nonce+grant. */
export async function verifyDeviceProof(devicePublicKey: string, proof: unknown, nonce: string, grantId: string): Promise<boolean> {
  if (typeof proof !== "string" || proof.length > 200) return false;
  try {
    const key = await crypto.subtle.importKey("spki", fromBase64(devicePublicKey), keyAlgorithm, false, ["verify"]);
    const signature = fromBase64Url(proof);
    if (signature.length !== 64) return false;
    return await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, signature, encoder.encode(proofMessage(nonce, grantId)));
  } catch {
    return false;
  }
}
