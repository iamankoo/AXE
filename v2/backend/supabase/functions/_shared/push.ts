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

export type RequestKind = "payment" | "invite";

export interface AdminNotice {
  requestId: string;
  kind: RequestKind;
}

/** The exact, minimal content of a new-request notification. */
export function noticeContent(notice: AdminNotice) {
  return {
    title: "AXE Admin",
    body: notice.kind === "payment"
      ? "New payment request received. Tap to review."
      : "New invitation request received. Tap to review.",
    // The only data the app receives: what happened and which request to go and fetch.
    data: { type: "new_request", requestId: notice.requestId },
  };
}

/** FCM HTTP v1 message for one device token. */
export function buildMessage(deviceToken: string, notice: AdminNotice) {
  const { title, body, data } = noticeContent(notice);
  return {
    message: {
      token: deviceToken,
      notification: { title, body },
      data,
      android: {
        priority: "HIGH",
        // `tag` makes a repeated notification for the same request replace itself instead of stacking.
        notification: { channel_id: "axe_requests", tag: notice.requestId },
      },
    },
  };
}

/**
 * What an FCM error response means for the stored token. Only a response that says THE TOKEN is bad may delete it;
 * payload mistakes, auth problems and outages must never remove working devices.
 */
export function classifyFcmFailure(status: number, text: string): "stale-token" | "other" {
  if (status === 404) return "stale-token"; // UNREGISTERED: app uninstalled, data cleared, or token rotated/deleted
  if (status === 403 && /SENDER_ID_MISMATCH/i.test(text)) return "stale-token"; // token belongs to another project
  if (status === 400 && /UNREGISTERED|registration token/i.test(text)) return "stale-token"; // malformed / invalid token
  return "other"; // 400 for a bad payload, 401/403 auth, 429, 5xx: keep the token
}

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
  try {
    const url = new URL(value);
    if (["localhost", "127.0.0.1", "host.docker.internal"].includes(url.hostname)) return value.replace(/\/$/, "");
  } catch { /* fall through */ }
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

    const sink = Deno.env.get("AXE_PUSH_TEST_SINK_URL");
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
