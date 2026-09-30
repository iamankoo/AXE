// Admin push notifications via Firebase Cloud Messaging (HTTP v1 API).
//
// FCM is used ONLY to notify the AXE Admin app. Credentials come from the
// FCM_SERVICE_ACCOUNT_JSON secret (a Firebase service-account key) and never leave the server.
// For local end-to-end tests without Firebase, AXE_PUSH_TEST_SINK_URL receives the same
// messages instead (development only; unset in production).

import { b64url } from "./crypto.ts";
import { db } from "./common.ts";

export interface AdminNotice {
  title: string;
  body: string;
  requestId: string;
  kind: "payment" | "invite";
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
  const response = await fetch("https://oauth2.googleapis.com/token", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "urn:ietf:params:oauth:grant-type:jwt-bearer",
      assertion: `${header}.${claims}.${b64url(signature)}`,
    }),
  });
  if (!response.ok) throw new Error(`FCM OAuth failed: ${response.status}`);
  const body = await response.json() as { access_token: string; expires_in: number };
  cachedToken = { value: body.access_token, expires: Date.now() + body.expires_in * 1000 };
  return body.access_token;
}

/** Notifies every signed-in AXE Admin device. Failures are logged, never fatal to the request. */
export async function notifyAdmins(notice: AdminNotice): Promise<void> {
  try {
    const { data: devices } = await db().from("admin_devices").select("token");
    const tokens = (devices ?? []).map((d) => d.token as string);

    const sink = Deno.env.get("AXE_PUSH_TEST_SINK_URL");
    if (sink) {
      await fetch(sink, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ...notice, tokens }) });
    }

    const sa = serviceAccount();
    if (!sa) {
      if (!sink) console.warn("FCM is not configured (FCM_SERVICE_ACCOUNT_JSON); admin notification skipped.");
      return;
    }

    const token = await accessToken(sa);
    await Promise.all(tokens.map(async (deviceToken) => {
      const response = await fetch(`https://fcm.googleapis.com/v1/projects/${sa.project_id}/messages:send`, {
        method: "POST",
        headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
        body: JSON.stringify({
          message: {
            token: deviceToken,
            notification: { title: notice.title, body: notice.body },
            data: { requestId: notice.requestId, kind: notice.kind },
            android: {
              priority: "HIGH",
              notification: { channel_id: "axe_requests", tag: notice.requestId, click_action: "OPEN_AXE_REQUEST" },
            },
          },
        }),
      });
      if (response.status === 404 || response.status === 400) {
        const text = await response.text();
        if (text.includes("UNREGISTERED") || text.includes("INVALID_ARGUMENT")) {
          await db().from("admin_devices").delete().eq("token", deviceToken); // app uninstalled / token rotated
        }
      } else if (!response.ok) {
        console.error("FCM send failed", response.status);
      }
    }));
  } catch (error) {
    console.error("admin notification failed", error instanceof Error ? error.message : error);
  }
}
