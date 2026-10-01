// Unit tests of the pure helpers used by the Edge Functions (supabase/functions/_shared/pure.ts). Plain Node, no stack needed:
//   node --test tests/pure.test.mjs        (Node 22.6+/24 strips the TypeScript types itself)

import { test } from "node:test";
import assert from "node:assert/strict";
import { buildMessage, classifyFcmFailure, clientIp, isLoopbackUrl, noticeContent, requireSecret } from "../supabase/functions/_shared/pure.ts";

const headers = (map) => ({ get: (name) => map[name.toLowerCase()] ?? null });

test("isLoopbackUrl accepts only this machine / the Docker host", () => {
  for (const ok of ["http://localhost:8799/", "http://127.0.0.1:54321", "http://host.docker.internal:8798/token", "https://localhost/x"]) {
    assert.equal(isLoopbackUrl(ok), true, ok);
  }
  for (const bad of [
    "https://fcm.googleapis.com", "http://evil.example/", "http://127.0.0.1.evil.com/", "http://localhost.evil.com/",
    "http://localhost@evil.com/", "http://evil.com#localhost", "http://[::1]x/", "file:///etc/passwd", "localhost", "", "not a url",
  ]) {
    assert.equal(isLoopbackUrl(bad), false, bad);
  }
});

test("classifyFcmFailure only treats a bad TOKEN as stale", () => {
  const stale = [
    [404, '{"error":{"status":"NOT_FOUND","details":[{"errorCode":"UNREGISTERED"}]}}'],
    [404, ""],
    [403, '{"error":{"message":"SenderId mismatch","details":[{"errorCode":"SENDER_ID_MISMATCH"}]}}'],
    [400, '{"error":{"status":"INVALID_ARGUMENT","message":"The registration token is not a valid FCM registration token"}}'],
    [400, "UNREGISTERED"],
  ];
  for (const [status, text] of stale) assert.equal(classifyFcmFailure(status, text), "stale-token", `${status} ${text}`);

  const keep = [
    [400, '{"error":{"status":"INVALID_ARGUMENT","message":"Invalid JSON payload received."}}'],
    [400, ""],
    [401, '{"error":{"status":"UNAUTHENTICATED"}}'],
    [403, '{"error":{"message":"The caller does not have permission"}}'],
    [429, ""], [500, ""], [503, "UNAVAILABLE"],
  ];
  for (const [status, text] of keep) assert.equal(classifyFcmFailure(status, text), "other", `${status} ${text}`);
});

test("requireSecret fails closed when the secret is missing or short", () => {
  for (const bad of [undefined, null, "", "short", "x".repeat(15)]) {
    assert.throws(() => requireSecret("AXE_HASH_SECRET", bad), /AXE_HASH_SECRET is not configured/, String(bad));
  }
  const good = "x".repeat(16);
  assert.equal(requireSecret("AXE_HASH_SECRET", good), good);
  assert.throws(() => requireSecret("S", "abc", 4 + 1), /at least 5/);
});

test("clientIp trusts the platform header, never the first x-forwarded-for entry", () => {
  assert.equal(clientIp(headers({ "cf-connecting-ip": "198.51.100.7", "x-forwarded-for": "6.6.6.6, 198.51.100.7" })), "198.51.100.7");
  assert.equal(clientIp(headers({ "cf-connecting-ip": " 198.51.100.7 " })), "198.51.100.7");
  assert.equal(clientIp(headers({ "x-real-ip": "203.0.113.9", "x-forwarded-for": "6.6.6.6" })), "203.0.113.9");
  // only a client-prefillable chain is available: the entry appended by the nearest proxy (last) wins
  assert.equal(clientIp(headers({ "x-forwarded-for": "6.6.6.6, 7.7.7.7, 203.0.113.50" })), "203.0.113.50");
  assert.equal(clientIp(headers({ "x-forwarded-for": "203.0.113.50" })), "203.0.113.50");
  assert.equal(clientIp(headers({})), "unknown");
  assert.equal(clientIp(headers({ "x-forwarded-for": " , " })), "unknown");
});

test("the notification content is exactly generic text plus the request id", () => {
  const id = "0b6f2a54-5a43-4f43-9a52-7c1d7c6f9a01";
  assert.deepEqual(noticeContent({ requestId: id, kind: "payment" }), {
    title: "AXE Admin", body: "New payment request received. Tap to review.", data: { type: "new_request", requestId: id },
  });
  assert.equal(noticeContent({ requestId: id, kind: "invite" }).body, "New invitation request received. Tap to review.");

  const message = buildMessage("device-token", { requestId: id, kind: "payment" });
  assert.deepEqual(message, {
    message: {
      token: "device-token",
      notification: { title: "AXE Admin", body: "New payment request received. Tap to review." },
      data: { type: "new_request", requestId: id },
      android: { priority: "HIGH", notification: { channel_id: "axe_requests", tag: id } },
    },
  });
});

test("the notification builders cannot carry request details even if they are passed in", () => {
  const hostile = { requestId: "r", kind: "payment", name: "Alice Example", utr: "UTR99999999", amountPaid: 149, code: "SECRETCODE", screenshot: "iVBOR" };
  const text = JSON.stringify([noticeContent(hostile), buildMessage("t", hostile)]);
  for (const secret of ["Alice", "UTR99999999", "149", "SECRETCODE", "iVBOR"]) assert.ok(!text.includes(secret), secret);
});
