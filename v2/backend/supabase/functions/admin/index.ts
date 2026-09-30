// AXE Admin API (called by the AXE Admin Android app).
//
// Every call needs a Supabase Auth session (Authorization: Bearer <access token>) whose user
// is listed in public.admins. Admin credentials exist only in Supabase Auth and on the
// admin's phone; AXE v2 contains none.
//
//   GET    /admin/requests                 pending requests (minimal fields)
//   GET    /admin/requests/:id             one request
//   GET    /admin/requests/:id/screenshot  the payment screenshot (streamed; admin only)
//   POST   /admin/requests/:id/decision    {"decision":"approve"|"reject"}
//   GET    /admin/grants                   active authorizations
//   POST   /admin/grants/:jti/revoke       end an active authorization early
//   POST   /admin/devices                  {"token": fcm token}   register for push
//   DELETE /admin/devices                  {"token": fcm token}   sign-out / unregister

import { cleanup, db, fail, json, PICKUP_TTL_MS, PLANS, route, SCREENSHOT_BUCKET } from "../_shared/common.ts";
import { hmacHex } from "../_shared/crypto.ts";
import { signClaims } from "../_shared/signing.ts";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

Deno.serve(async (req) => {
  try {
    const admin = await authenticate(req);
    if (!admin) return fail(401, "Sign in to AXE Admin.");

    const path = route(req, "admin");
    const [section, id, action] = path;
    if (section === "requests") {
      if (req.method === "GET" && path.length === 1) return await listRequests();
      if (req.method === "GET" && path.length === 2) return await getRequest(id);
      if (req.method === "GET" && path.length === 3 && action === "screenshot") return await screenshot(id);
      if (req.method === "POST" && path.length === 3 && action === "decision") return await decide(req, id, admin);
    }
    if (section === "grants") {
      if (req.method === "GET" && path.length === 1) return await listGrants();
      if (req.method === "POST" && path.length === 3 && action === "revoke") return await revoke(id);
    }
    if (section === "devices" && path.length === 1) {
      if (req.method === "POST") return await registerDevice(req, admin);
      if (req.method === "DELETE") return await unregisterDevice(req, admin);
    }
    return fail(404, "Not found.");
  } catch (error) {
    console.error("admin error", error instanceof Error ? error.message : error);
    return fail(500, "The AXE server hit a problem. Please try again.");
  }
});

/** Returns the admin's user id, or null unless the bearer token belongs to a listed admin. */
async function authenticate(req: Request): Promise<string | null> {
  const header = req.headers.get("authorization") ?? "";
  const token = header.startsWith("Bearer ") ? header.slice(7) : "";
  if (!token) return null;
  const { data, error } = await db().auth.getUser(token);
  if (error || !data.user) return null;
  const { data: row } = await db().from("admins").select("user_id").eq("user_id", data.user.id).maybeSingle();
  return row ? data.user.id : null;
}

async function listRequests(): Promise<Response> {
  await cleanup();
  const { data, error } = await db().from("access_requests")
    .select("id, kind, plan, expected_amount, name, amount_paid, utr, invite_code, duplicate_utr, created_at, expires_at")
    .eq("status", "pending")
    .gt("expires_at", new Date().toISOString())
    .order("created_at", { ascending: true })
    .limit(100);
  if (error) throw new Error(error.message);
  return json(200, { requests: (data ?? []).map(present) });
}

async function getRequest(id: string): Promise<Response> {
  if (!UUID.test(id)) return fail(404, "Request not found.");
  const { data } = await db().from("access_requests")
    .select("id, kind, status, plan, expected_amount, name, amount_paid, utr, invite_code, duplicate_utr, screenshot_path, created_at, expires_at")
    .eq("id", id).maybeSingle();
  if (!data || data.status !== "pending") return fail(404, "This request was already handled or has expired.");

  return json(200, { request: { ...present(data), hasScreenshot: !!data.screenshot_path } });
}

/**
 * Streams the screenshot to the signed-in admin. There is no public or pre-signed URL: the
 * image is only reachable through this authenticated call, and only while the request is pending.
 */
async function screenshot(id: string): Promise<Response> {
  if (!UUID.test(id)) return fail(404, "Not found.");
  const { data } = await db().from("access_requests").select("status, screenshot_path").eq("id", id).maybeSingle();
  if (!data?.screenshot_path || data.status !== "pending") return fail(404, "No screenshot.");
  const { data: file, error } = await db().storage.from(SCREENSHOT_BUCKET).download(data.screenshot_path);
  if (error || !file) return fail(404, "No screenshot.");
  return new Response(file, {
    headers: { "Content-Type": file.type || "application/octet-stream", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" },
  });
}

function present(r: Record<string, unknown>) {
  const plan = PLANS[r.plan as string];
  return {
    id: r.id,
    kind: r.kind,
    name: r.name,
    plan: r.plan,
    planLabel: plan?.label ?? r.plan,
    expectedAmount: r.expected_amount,
    amountPaid: r.amount_paid === null ? null : Number(r.amount_paid),
    amountMismatch: r.kind === "payment" && Number(r.amount_paid) !== Number(r.expected_amount),
    utr: r.utr,
    inviteCode: r.invite_code,
    duplicateUtr: r.duplicate_utr,
    createdAt: r.created_at,
    expiresAt: r.expires_at,
  };
}

async function decide(req: Request, id: string, adminId: string): Promise<Response> {
  if (!UUID.test(id)) return fail(404, "Request not found.");
  const body = await req.json().catch(() => null) as { decision?: string } | null;
  const decision = body?.decision;
  if (decision !== "approve" && decision !== "reject") return fail(400, "Decision must be approve or reject.");

  const { data: request } = await db().from("access_requests")
    .select("id, kind, status, plan, device_hash, utr, screenshot_path, expires_at").eq("id", id).maybeSingle();
  if (!request || request.status !== "pending" || new Date(request.expires_at).getTime() < Date.now()) {
    return fail(409, "This request was already handled or has expired.");
  }

  const now = Date.now();
  let grantToken: string | null = null;
  let grantId: string | null = null;
  let expiresAt: number | null = null;
  if (decision === "approve") {
    const plan = PLANS[request.plan as string];
    const jti = crypto.randomUUID();
    grantId = jti;
    expiresAt = now + plan.hours * 3600_000; // the timer starts at approval, on the server clock
    grantToken = await signClaims({
      typ: "axe-grant",
      jti,
      rid: id,
      plan: request.plan,
      iat: now,
      exp: expiresAt,
      did: request.device_hash,
    });
    const { error } = await db().from("access_grants").insert({
      jti,
      request_id: id,
      plan: request.plan,
      device_hash: request.device_hash,
      issued_at: new Date(now).toISOString(),
      expires_at: new Date(expiresAt).toISOString(),
    });
    if (error) throw new Error(error.message);
    if (request.kind === "payment" && request.utr) {
      const refHash = await hmacHex(Deno.env.get("AXE_HASH_SECRET") ?? "", String(request.utr).toUpperCase());
      await db().from("used_references").upsert({ ref_hash: refHash, used_at: new Date(now).toISOString() });
    }
  }

  // Conditional update: only a still-pending request can be decided (no double decisions).
  // The decided row becomes a minimal RESULT STUB for the Windows client (id, poll-token hash, status, signed
  // grant). All request/payment data is cleared here, server-side, regardless of what the admin app does next.
  // The stub itself is deleted the moment the client picks up its result (see access/index.ts), with
  // PICKUP_TTL_MS as the hard cap if the PC never comes back. screenshot_path is kept until the object is
  // really gone, so a failed storage delete can still be swept later instead of orphaning the file.
  const { data: updated, error: updateError } = await db().from("access_requests").update({
    status: decision === "approve" ? "approved" : "rejected",
    grant_token: grantToken,
    decided_at: new Date(now).toISOString(),
    expires_at: new Date(now + PICKUP_TTL_MS).toISOString(),
    name: null,
    amount_paid: null,
    utr: null,
    invite_code: null,
    duplicate_utr: false,
  }).eq("id", id).eq("status", "pending").select("id");
  if (updateError) throw new Error(updateError.message);
  if (!updated?.length) {
    if (grantId) await db().from("access_grants").delete().eq("jti", grantId); // lost a race with another decision
    return fail(409, "This request was already handled.");
  }

  if (request.screenshot_path) {
    const { error: removeError } = await db().storage.from(SCREENSHOT_BUCKET).remove([request.screenshot_path]);
    if (removeError) {
      console.error("screenshot delete failed; it stays referenced for the pickup sweep", removeError.message);
    } else {
      await db().from("access_requests").update({ screenshot_path: null }).eq("id", id);
    }
  }
  console.log(`request ${id} ${decision}d by admin ${adminId.slice(0, 8)}`);
  return json(200, { ok: true, decision, expiresAt });
}

async function listGrants(): Promise<Response> {
  const { data, error } = await db().from("access_grants")
    .select("jti, plan, issued_at, expires_at")
    .is("revoked_at", null)
    .gt("expires_at", new Date().toISOString())
    .order("expires_at", { ascending: true });
  if (error) throw new Error(error.message);
  return json(200, {
    grants: (data ?? []).map((g) => ({
      id: g.jti,
      plan: g.plan,
      planLabel: PLANS[g.plan]?.label ?? g.plan,
      issuedAt: g.issued_at,
      expiresAt: g.expires_at,
    })),
  });
}

async function revoke(jti: string): Promise<Response> {
  if (!UUID.test(jti)) return fail(404, "Not found.");
  await db().from("access_grants").update({ revoked_at: new Date().toISOString() }).eq("jti", jti).is("revoked_at", null);
  return json(200, { ok: true });
}

async function registerDevice(req: Request, adminId: string): Promise<Response> {
  const body = await req.json().catch(() => null) as { token?: string } | null;
  const token = body?.token;
  if (typeof token !== "string" || token.length < 20 || token.length > 4096) return fail(400, "Bad token.");
  await db().from("admin_devices").upsert({ token, user_id: adminId, updated_at: new Date().toISOString() });
  return json(200, { ok: true });
}

async function unregisterDevice(req: Request, adminId: string): Promise<Response> {
  const body = await req.json().catch(() => null) as { token?: string } | null;
  if (typeof body?.token === "string") {
    await db().from("admin_devices").delete().eq("token", body.token).eq("user_id", adminId);
  }
  return json(200, { ok: true });
}
