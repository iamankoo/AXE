// Pure, dependency-free helpers of the AXE Edge Functions (no Deno APIs, no imports), so they can be unit-tested with
// plain Node (backend/tests/pure.test.mjs) as well as run by the Edge runtime. Only erasable TypeScript is used here.

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

/** True for URLs that cannot leave this machine / the local Docker host. Development overrides must pass this. */
export function isLoopbackUrl(value: string): boolean {
  try {
    return ["localhost", "127.0.0.1", "host.docker.internal"].includes(new URL(value).hostname);
  } catch {
    return false;
  }
}

/**
 * A server secret that must be present: an unset or short value would silently make keyed hashes guessable, so callers
 * fail closed instead of running with a default.
 */
export function requireSecret(name: string, value: string | undefined | null, minLength = 16): string {
  const secret = value ?? "";
  if (secret.length < minLength) throw new Error(`${name} is not configured (at least ${minLength} characters required)`);
  return secret;
}

/**
 * The client's network address as seen by the platform, not as claimed by the client. `cf-connecting-ip` is set by the
 * edge proxy itself; `x-forwarded-for` is a chain a client can pre-fill, so when it is the only source the LAST entry
 * (appended by the nearest proxy) is used, never the first.
 */
export function clientIp(headers: { get(name: string): string | null }): string {
  const single = headers.get("cf-connecting-ip") || headers.get("x-real-ip");
  if (single) return single.trim();
  const chain = headers.get("x-forwarded-for")?.split(",").map((s) => s.trim()).filter(Boolean);
  return chain?.length ? chain[chain.length - 1] : "unknown";
}
