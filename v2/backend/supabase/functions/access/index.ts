// AXE v2 public access API (called by the Windows client).
//
//   POST   /access/requests        submit a payment or invitation request
//   GET    /access/requests/:id    poll the decision (x-poll-token header, ?nonce=) → signed status
//   DELETE /access/requests/:id    withdraw a pending request, or acknowledge a decided result (deletes it at once)
//   POST   /access/session         validate a grant → signed server time + validity
//
// The server alone decides: invitation codes are checked here (never in the client), every
// request needs the admin's manual approval, and grants/time are ECDSA-signed.

import { b64urlDecode, hmacHex, randomToken, sha256Hex, timingSafeEqual } from "../_shared/crypto.ts";
import {
  background, cleanup, clientKey, db, fail, json, PENDING_TTL_MS, PLANS, rateLimited, RESULT_GRACE_MS, route, SCREENSHOT_BUCKET,
} from "../_shared/common.ts";
import { signClaims, verifyClaims } from "../_shared/signing.ts";
import { notifyAdmins } from "../_shared/push.ts";

const MAX_BODY = 8 * 1024 * 1024; // 5 MB screenshot as base64 + fields
const MAX_SCREENSHOT = 5 * 1024 * 1024;
const NAME = /^[^\p{Cc}\p{Cf}]{2,60}$/u;
const UTR = /^[A-Za-z0-9]{6,30}$/;
const CODE = /^[A-Z0-9]{3,32}$/;
const DEVICE = /^[0-9a-f]{64}$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const NONCE = /^[A-Za-z0-9_-]{16,64}$/;

Deno.serve(async (req) => {
  try {
    const path = route(req, "access");
    if (req.method === "POST" && path.length === 1 && path[0] === "requests") return await submit(req);
    if (req.method === "GET" && path.length === 2 && path[0] === "requests") return await status(req, path[1]);
    if (req.method === "DELETE" && path.length === 2 && path[0] === "requests") return await cancel(req, path[1]);
    if (req.method === "POST" && path.length === 1 && path[0] === "session") return await session(req);
    return fail(404, "Not found.");
  } catch (error) {
    console.error("access error", error instanceof Error ? error.message : error);
    return fail(500, "The AXE server hit a problem. Please try again.");
  }
});

async function readJson(req: Request): Promise<Record<string, unknown> | null> {
  const length = Number(req.headers.get("content-length") ?? "0");
  if (length > MAX_BODY) return null;
  const text = await req.text();
  if (text.length > MAX_BODY) return null;
  try {
    const value = JSON.parse(text);
    return value && typeof value === "object" && !Array.isArray(value) ? value : null;
  } catch {
    return null;
  }
}

function sniffImage(bytes: Uint8Array): string | null {
  const starts = (sig: number[]) => sig.every((b, i) => bytes[i] === b);
  if (starts([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])) return "image/png";
  if (starts([0xff, 0xd8, 0xff])) return "image/jpeg";
  if (starts([0x52, 0x49, 0x46, 0x46]) && String.fromCharCode(...bytes.slice(8, 12)) === "WEBP") return "image/webp";
  return null;
}

async function validInvitation(code: string): Promise<boolean> {
  const secret = Deno.env.get("AXE_HASH_SECRET") ?? "";
  const given = await hmacHex(secret, code);
  const codes = (Deno.env.get("AXE_INVITE_CODES") ?? "").split(",").map((c) => c.trim().toUpperCase()).filter(Boolean);
  let match = false;
  for (const valid of codes) match = timingSafeEqual(given, await hmacHex(secret, valid)) || match;
  return match;
}

/** Accepted submissions per hour per network and per PC (AXE_SUBMIT_LIMIT overrides; 10 by default). */
const SUBMIT_LIMIT = Number(Deno.env.get("AXE_SUBMIT_LIMIT") ?? "") || 10;

async function submit(req: Request): Promise<Response> {
  await cleanup();
  const body = await readJson(req);
  if (!body) return fail(413, "The request is too large or malformed. Screenshots can be up to 5 MB.");

  const kind = body.kind;
  const name = typeof body.name === "string" ? body.name.trim() : "";
  const planId = typeof body.plan === "string" ? body.plan : "";
  const device = typeof body.device === "string" ? body.device : "";
  const plan = PLANS[planId];
  if (kind !== "payment" && kind !== "invite") return fail(400, "Unknown request type.");
  if (!NAME.test(name)) return fail(400, "Please enter your name (2–60 characters).");
  if (!plan) return fail(400, "Please choose a plan.");
  if (!DEVICE.test(device)) return fail(400, "This copy of AXE couldn't identify itself. Please reinstall AXE v2.");

  const pollToken = randomToken();
  const row: Record<string, unknown> = {
    kind,
    plan: planId,
    expected_amount: plan.price,
    name,
    device_hash: device,
    poll_token_hash: await sha256Hex(pollToken),
    expires_at: new Date(Date.now() + PENDING_TTL_MS).toISOString(),
  };

  let screenshot: { bytes: Uint8Array; type: string } | null = null;
  if (kind === "payment") {
    const amount = typeof body.amountPaid === "number" ? body.amountPaid : Number.NaN;
    const utr = typeof body.utr === "string" ? body.utr.trim() : "";
    const shot = body.screenshot as { type?: unknown; data?: unknown } | undefined;
    if (!Number.isFinite(amount) || amount <= 0 || amount > 100000) return fail(400, "Please enter the amount you paid.");
    if (!UTR.test(utr)) return fail(400, "Please enter the UTR / reference number (6–30 letters or digits).");
    if (typeof shot?.data !== "string") return fail(400, "Please attach the payment screenshot.");
    let bytes: Uint8Array;
    try {
      bytes = Uint8Array.from(atob(shot.data), (c) => c.charCodeAt(0));
    } catch {
      return fail(400, "The screenshot couldn't be read.");
    }
    const type = sniffImage(bytes);
    if (!type) return fail(400, "The screenshot must be a PNG, JPEG or WebP image.");
    if (bytes.length > MAX_SCREENSHOT) return fail(413, "The screenshot is larger than 5 MB.");
    screenshot = { bytes, type };

    const refHash = await hmacHex(Deno.env.get("AXE_HASH_SECRET") ?? "", utr.toUpperCase());
    const [{ data: used }, { data: pendingSame }] = await Promise.all([
      db().from("used_references").select("ref_hash").eq("ref_hash", refHash).maybeSingle(),
      db().from("access_requests").select("id").eq("status", "pending").eq("utr", utr).limit(1),
    ]);
    row.amount_paid = Math.round(amount * 100) / 100;
    row.utr = utr;
    row.duplicate_utr = !!used || !!pendingSame?.length;
  } else {
    const code = typeof body.code === "string" ? body.code.trim().toUpperCase() : "";
    if (!CODE.test(code) || !(await validInvitation(code))) {
      return fail(400, "That invitation code isn't valid.");
    }
    row.invite_code = code;
  }

  // Rate limits apply to well-formed submissions (malformed ones are refused above without
  // touching the database), before anything is stored or anyone is notified.
  if (await rateLimited(`submit:${await clientKey(req)}`, SUBMIT_LIMIT, 3600)) {
    return fail(429, "Too many requests from this network. Please wait a while and try again.");
  }
  if (await rateLimited(`submit-device:${device}`, SUBMIT_LIMIT, 3600)) {
    return fail(429, "Too many requests from this PC. Please wait a while and try again.");
  }

  // One pending request per PC: a new submission replaces the previous one.
  const { data: previous } = await db().from("access_requests")
    .select("id, screenshot_path").eq("device_hash", device).eq("status", "pending");
  if (previous?.length) {
    const paths = previous.map((p) => p.screenshot_path).filter((p): p is string => !!p);
    if (paths.length) await db().storage.from(SCREENSHOT_BUCKET).remove(paths);
    await db().from("access_requests").delete().in("id", previous.map((p) => p.id));
  }

  const { data: inserted, error } = await db().from("access_requests").insert(row).select("id").single();
  if (error || !inserted) throw new Error(`insert failed: ${error?.message}`);
  const id = inserted.id as string;

  if (screenshot) {
    const path = `${id}/${crypto.randomUUID()}`;
    const { error: uploadError } = await db().storage.from(SCREENSHOT_BUCKET)
      .upload(path, screenshot.bytes, { contentType: screenshot.type, upsert: false });
    if (uploadError) {
      await db().from("access_requests").delete().eq("id", id);
      throw new Error(`screenshot upload failed: ${uploadError.message}`);
    }
    await db().from("access_requests").update({ screenshot_path: path }).eq("id", id);
  }

  // The request is stored and valid. Tell the admin app in the background: the notification is a doorbell carrying
  // only generic text and the request id, and whether it is delivered never affects this response.
  background(notifyAdmins({ requestId: id, kind: kind as "payment" | "invite" }));

  return json(201, { requestId: id, pollToken });
}

/** Loads a request after checking the caller holds its poll secret. */
async function authorizedRequest(req: Request, id: string) {
  const token = req.headers.get("x-poll-token") ?? "";
  if (!UUID.test(id) || token.length < 20 || token.length > 100) return null;
  const { data } = await db().from("access_requests")
    .select("id, status, poll_token_hash, grant_token, expires_at, screenshot_path").eq("id", id).maybeSingle();
  if (!data || !timingSafeEqual(data.poll_token_hash, await sha256Hex(token))) return null;
  return data;
}

async function status(req: Request, id: string): Promise<Response> {
  const nonce = new URL(req.url).searchParams.get("nonce") ?? "";
  if (!NONCE.test(nonce)) return fail(400, "Bad request.");
  if (await rateLimited(`poll:${await clientKey(req)}`, 120, 60)) return fail(429, "Too many requests.");
  await cleanup().catch((e) => console.error("cleanup failed", e instanceof Error ? e.message : e)); // at most once a minute

  const request = await authorizedRequest(req, id);
  if (!request) return fail(404, "This request is no longer active.");

  let state = request.status as string;
  if (state === "pending" && new Date(request.expires_at).getTime() < Date.now()) {
    if (request.screenshot_path) await db().storage.from(SCREENSHOT_BUCKET).remove([request.screenshot_path]);
    await db().from("access_requests").delete().eq("id", id);
    state = "expired";
  }

  const token = await signClaims({
    typ: "axe-status",
    rid: id,
    nonce,
    status: state,
    now: Date.now(),
    grant: state === "approved" ? request.grant_token : null,
  });

  // Reliable delivery: reading a decided result does NOT delete it, so a retry after a lost response (or a client
  // crash before it saved the grant) gets the same signed result again. The client acknowledges once the result is
  // safely stored (DELETE below), which removes the stub at once. A client that never acknowledges cannot keep it
  // past RESULT_GRACE_MS after the first read; the expiry cleanup then deletes it. The token is signed first, so a
  // failure above changes nothing.
  if (state === "approved" || state === "rejected") {
    const graceEnd = Date.now() + RESULT_GRACE_MS;
    if (new Date(request.expires_at).getTime() > graceEnd) {
      const { error } = await db().from("access_requests")
        .update({ expires_at: new Date(graceEnd).toISOString() })
        .eq("id", id).in("status", ["approved", "rejected"]);
      if (error) console.error("could not shorten the result grace period", error.message);
    }
  }
  return json(200, { token });
}

/**
 * DELETE /access/requests/:id, used for two things by the poll-secret holder: withdrawing a pending request, and
 * ACKNOWLEDGING a decided one (approved / rejected) once the client has stored the result. Either way the row and any
 * screenshot are deleted at once. Unknown or already-deleted ids answer ok, so acknowledging is idempotent.
 */
async function cancel(req: Request, id: string): Promise<Response> {
  const request = await authorizedRequest(req, id);
  if (!request) return json(200, { ok: true }); // already gone
  if (request.screenshot_path) await db().storage.from(SCREENSHOT_BUCKET).remove([request.screenshot_path]);
  await db().from("access_requests").delete().eq("id", id).in("status", ["pending", "approved", "rejected"]);
  return json(200, { ok: true });
}

async function session(req: Request): Promise<Response> {
  if (await rateLimited(`session:${await clientKey(req)}`, 60, 60)) return fail(429, "Too many requests.");
  const body = await readJson(req);
  const nonce = typeof body?.nonce === "string" ? body.nonce : "";
  if (!NONCE.test(nonce)) return fail(400, "Bad request.");

  const now = Date.now();
  const grant = await verifyClaims<{ typ: string; jti: string; exp: number }>(body?.grant, "axe-grant");
  let valid = false;
  let reason: string | null = "invalid";
  let exp = 0;
  if (grant && UUID.test(grant.jti)) {
    const { data } = await db().from("access_grants").select("expires_at, revoked_at").eq("jti", grant.jti).maybeSingle();
    exp = grant.exp;
    if (!data) reason = "expired";
    else if (data.revoked_at) reason = "revoked";
    else if (new Date(data.expires_at).getTime() <= now || grant.exp <= now) reason = "expired";
    else {
      valid = true;
      reason = null;
    }
  }

  const token = await signClaims({ typ: "axe-session", nonce, jti: grant?.jti ?? "", valid, reason, now, exp });
  return json(200, { token });
}
