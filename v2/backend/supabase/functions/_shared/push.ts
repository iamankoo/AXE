// Admin push notifications via Firebase Cloud Messaging (HTTP v1 API).
//
// FCM is used ONLY to tell the AXE Admin app "a new request arrived". It is a doorbell, never a data channel:
//   * the notification carries generic text and the request id, nothing else (no name, amount, UTR, code, screenshot);
//   * the Admin app fetches the real request from the authenticated admin API after the tap;
//   * a failed or skipped notification never affects the request (see notifyAdmins / background()).
//
// Credentials come from the FCM_SERVICE_ACCOUNT_JSON secret (a Firebase service-account key) and never leave the
// server. For local development without Firebase, AXE_PUSH_TEST_SINK_URL receives the same message, and
// AXE_FCM_API_BASE / AXE_FCM_OAUTH_URL can point the sender at a local fake FCM server (loopback hosts only; unset in
// production).

import { b64url } from "./crypto.ts";
import { db } from "./common.ts";
import { buildMessage, classifyFcmFailure, isLoopbackUrl, noticeContent } from "./pure.ts";
import type { AdminNotice } from "./pure.ts";

export type { AdminNotice, RequestKind } from "./pure.ts";

interface ServiceAccount {
  project_id: string;
  client_email: string;
  private_key: string;
}

const encoder = new TextEncoder();
let cachedToken: { value: string; expires: number } | null = null;

function serviceAccount(): ServiceAccount | null {
  const raw = Deno.env.get("FCM_SERVICE_ACCOUNT_JSON");
  if (!raw) return null;
  const sa = JSON.parse(raw) as ServiceAccount;
  return sa.project_id && sa.client_email && sa.private_key ? sa : null;
}

/** Development override for the FCM endpoints, honoured only for loopback / Docker-host URLs. */
function endpoint(envName: string, fallback: string): string {
  const value = Deno.env.get(envName);
  if (!value) return fallback;
  if (isLoopbackUrl(value)) return value.replace(/\/$/, "");
  console.warn(`${envName} ignored: only loopback URLs are accepted.`);
  return fallback;
}

const fcmBase = () => endpoint("AXE_FCM_API_BASE", "https://fcm.googleapis.com");
const oauthUrl = () => endpoint("AXE_FCM_OAUTH_URL", "https://oauth2.googleapis.com/token");
const TIMEOUT_MS = 8000;

/** OAuth2 access token for FCM from a service-account JWT (RS256), cached until near expiry. */
async function accessToken(sa: ServiceAccount): Promise<string> {
  if (cachedToken && cachedToken.expires > Date.now() + 60_000) return cachedToken.value;
  const now = Math.floor(Date.now() / 1000);
  const header = b64url(encoder.encode(JSON.stringify({ alg: "RS256", typ: "JWT" })));
  const claims = b64url(encoder.encode(JSON.stringify({
    iss: sa.client_email,
    scope: "https://www.googleapis.com/auth/firebase.messaging",
    aud: "https://oauth2.googleapis.com/token",
    iat: now,
    exp: now + 3600,
  })));
  const pem = sa.private_key.replace(/-----[^-]+-----/g, "").replace(/\s+/g, "");
  const der = Uint8Array.from(atob(pem), (c) => c.charCodeAt(0));
  const key = await crypto.subtle.importKey("pkcs8", der, { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["sign"]);
  const signature = new Uint8Array(await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key, encoder.encode(`${header}.${claims}`)));
  const response = await fetch(oauthUrl(), {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "urn:ietf:params:oauth:grant-type:jwt-bearer",
      assertion: `${header}.${claims}.${b64url(signature)}`,
    }),
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });
  if (!response.ok) throw new Error(`FCM OAuth failed: ${response.status}`);
  const body = await response.json() as { access_token: string; expires_in: number };
  cachedToken = { value: body.access_token, expires: Date.now() + body.expires_in * 1000 };
  return body.access_token;
}

/**
 * Notifies every registered AXE Admin device that a new request arrived (best effort).
 *
 * This NEVER throws and is meant to run in the background: a notification is not part of creating a request, so a
 * missing configuration, an FCM outage or a bad token can only ever cost the notification. Nothing sensitive is
 * logged (no tokens, credentials or request contents).
 */
export async function notifyAdmins(notice: AdminNotice): Promise<void> {
  try {
    const { data: devices } = await db().from("admin_devices").select("token");
    const tokens = [...new Set((devices ?? []).map((d) => d.token as string))]; // one message per device

    // The development sink receives device tokens, so it is honoured only for loopback URLs: a mistakenly set or
    // tampered production value can never send tokens to an outside host.
    const sinkValue = Deno.env.get("AXE_PUSH_TEST_SINK_URL");
    const sink = sinkValue && isLoopbackUrl(sinkValue) ? sinkValue : "";
    if (sinkValue && !sink) console.warn("AXE_PUSH_TEST_SINK_URL ignored: only loopback URLs are accepted.");
    if (sink) {
      const { title, body, data } = noticeContent(notice);
      await fetch(sink, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ title, body, data, tokens }),
        signal: AbortSignal.timeout(TIMEOUT_MS),
      }).catch(() => {}); // a missing dev sink must not stop the real send below
    }

    const sa = serviceAccount();
    if (!sa) {
      if (!sink) console.warn("FCM is not configured (FCM_SERVICE_ACCOUNT_JSON); admin notification skipped.");
      return;
    }
    if (!tokens.length) return;

    const bearer = await accessToken(sa);
    let failed = 0;
    await Promise.all(tokens.map(async (deviceToken) => {
      try {
        const response = await fetch(`${fcmBase()}/v1/projects/${sa.project_id}/messages:send`, {
          method: "POST",
          headers: { Authorization: `Bearer ${bearer}`, "Content-Type": "application/json" },
          body: JSON.stringify(buildMessage(deviceToken, notice)),
          signal: AbortSignal.timeout(TIMEOUT_MS),
        });
        if (response.ok) return;
        failed++;
        const text = await response.text();
        if (classifyFcmFailure(response.status, text) === "stale-token") {
          await db().from("admin_devices").delete().eq("token", deviceToken); // app uninstalled / token rotated
        } else {
          console.error("FCM send failed", response.status);
        }
      } catch (error) {
        failed++;
        console.error("FCM send error", error instanceof Error ? error.name : "unknown");
      }
    }));
    if (failed) console.warn(`admin notification: ${failed} of ${tokens.length} device sends failed`);
  } catch (error) {
    console.error("admin notification failed", error instanceof Error ? error.message : error);
  }
}
