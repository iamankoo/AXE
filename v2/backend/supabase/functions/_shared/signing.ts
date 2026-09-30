// ECDSA P-256 / SHA-256 signing of AXE authorization messages.
//
// Token format: base64url(JSON) "." base64url(signature), signature in IEEE P1363 (r||s) form,
// which is what Web Crypto produces and what .NET's ECDsa.VerifyData expects. The private key
// (a JWK) lives only in the AXE_SIGNING_PRIVATE_JWK secret; AXE v2 ships only the public key.

import { b64url, b64urlDecode } from "./crypto.ts";

const encoder = new TextEncoder();
const decoder = new TextDecoder();
const algorithm = { name: "ECDSA", namedCurve: "P-256" } as const;
const signParams = { name: "ECDSA", hash: "SHA-256" } as const;

let keys: Promise<{ sign: CryptoKey; verify: CryptoKey }> | null = null;

function loadKeys() {
  keys ??= (async () => {
    const raw = Deno.env.get("AXE_SIGNING_PRIVATE_JWK");
    if (!raw) throw new Error("AXE_SIGNING_PRIVATE_JWK is not configured");
    const jwk = JSON.parse(raw) as JsonWebKey;
    if (jwk.kty !== "EC" || jwk.crv !== "P-256" || !jwk.d) throw new Error("AXE signing key must be a private P-256 JWK");
    const sign = await crypto.subtle.importKey("jwk", jwk, algorithm, false, ["sign"]);
    const verify = await crypto.subtle.importKey("jwk", { kty: "EC", crv: "P-256", x: jwk.x, y: jwk.y }, algorithm, false, ["verify"]);
    return { sign, verify };
  })();
  return keys;
}

export async function signClaims(claims: Record<string, unknown>): Promise<string> {
  const payload = b64url(encoder.encode(JSON.stringify(claims)));
  const signature = await crypto.subtle.sign(signParams, (await loadKeys()).sign, encoder.encode(payload));
  return `${payload}.${b64url(new Uint8Array(signature))}`;
}

/** Returns the claims if the token carries a valid AXE signature and the expected type, else null. */
export async function verifyClaims<T extends { typ?: string }>(token: unknown, typ: string): Promise<T | null> {
  if (typeof token !== "string" || token.length > 16_384) return null;
  const parts = token.split(".");
  if (parts.length !== 2) return null;
  try {
    const ok = await crypto.subtle.verify(signParams, (await loadKeys()).verify, b64urlDecode(parts[1]), encoder.encode(parts[0]));
    if (!ok) return null;
    const claims = JSON.parse(decoder.decode(b64urlDecode(parts[0]))) as T;
    return claims?.typ === typ ? claims : null;
  } catch {
    return null;
  }
}
