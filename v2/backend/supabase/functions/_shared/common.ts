// Shared plumbing for the AXE Edge Functions: plans, responses, database client, rate limits, cleanup.

import { createClient, type SupabaseClient } from "jsr:@supabase/supabase-js@2";
import { sha256Hex } from "./crypto.ts";

/** Plans are decided here, on the server; the client's displayed price is never trusted. */
export const PLANS: Record<string, { label: string; hours: number; price: number }> = {
  "1h": { label: "1 Hour", hours: 1, price: 149 },
  "5h": { label: "5 Hours", hours: 5, price: 199 },
  "10h": { label: "10 Hours", hours: 10, price: 299 },
};

export const SCREENSHOT_BUCKET = "payment-screenshots";
/** How long an unanswered request waits for the admin. */
export const PENDING_TTL_MS = 24 * 60 * 60 * 1000;
/** How long a decided request's result (no personal data) stays available to the client. */
export const PICKUP_TTL_MS = 24 * 60 * 60 * 1000;
/**
 * Once the Windows client has read a decided result, the result stub is kept at most this much longer in case the
 * client never acknowledges (crash, lost connection). Normally the client's acknowledgement deletes it at once.
 */
export const RESULT_GRACE_MS = 15 * 60 * 1000;

export function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" },
  });
}

export const fail = (status: number, error: string) => json(status, { error });

let admin: SupabaseClient | null = null;

/** Service-role client. Only ever used inside these functions, never exposed to clients. */
export function db(): SupabaseClient {
  admin ??= createClient(
    Deno.env.get("SUPABASE_URL")!,
    Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
    { auth: { persistSession: false, autoRefreshToken: false } },
  );
  return admin;
}

/** Path segments after the function name, e.g. /access/requests/abc → ["requests", "abc"]. */
export function route(req: Request, fn: string): string[] {
  const parts = new URL(req.url).pathname.split("/").filter(Boolean);
  const at = parts.indexOf(fn);
  return at >= 0 ? parts.slice(at + 1) : parts;
}

/** Client IP, hashed with a server secret so raw IPs are never stored. */
export async function clientKey(req: Request): Promise<string> {
  const ip = req.headers.get("x-forwarded-for")?.split(",")[0]?.trim()
    || req.headers.get("x-real-ip")
    || "unknown";
  return (await sha256Hex(`${Deno.env.get("AXE_HASH_SECRET") ?? ""}|${ip}`)).slice(0, 32);
}

/** True when the key exceeded `limit` hits within the window. */
export async function rateLimited(key: string, limit: number, windowSeconds: number): Promise<boolean> {
  const { data, error } = await db().rpc("axe_rate_hit", { p_key: key, p_window_seconds: windowSeconds });
  if (error) {
    console.error("rate limit check failed", error.message);
    return false; // fail open on the limiter only; every other check still applies
  }
  return (data as number) > limit;
}

let lastCleanup = 0;

/**
 * Deletes expired requests (and their screenshots) and expired grants. Runs opportunistically
 * at most once a minute per function instance; can also be scheduled (see docs).
 */
export async function cleanup(force = false): Promise<void> {
  const now = Date.now();
  if (!force && now - lastCleanup < 60_000) return;
  lastCleanup = now;
  const { data: expired } = await db()
    .from("access_requests")
    .select("id, screenshot_path")
    .lt("expires_at", new Date(now).toISOString())
    .limit(200);
  if (expired?.length) {
    const paths = expired.map((r) => r.screenshot_path).filter((p): p is string => !!p);
    if (paths.length) await db().storage.from(SCREENSHOT_BUCKET).remove(paths);
    await db().from("access_requests").delete().in("id", expired.map((r) => r.id));
  }
  await db().rpc("axe_cleanup_rows");
}
