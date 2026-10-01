// verify-fcm.mjs against a LOCAL fake FCM server (nothing here touches Google): proves the tool's checks and that it
// never prints secrets. It does not prove anything about real Firebase credentials.

import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { generateKeyPairSync } from "node:crypto";
import { createServer } from "node:http";
import { execFile } from "node:child_process";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const script = join(dirname(fileURLToPath(import.meta.url)), "..", "scripts", "verify-fcm.mjs");
const dir = mkdtempSync(join(tmpdir(), "axe-verify-fcm-"));
const seen = [];
let server;
let port;
let mode = "ok";

before(async () => {
  server = createServer((req, res) => {
    let data = "";
    req.on("data", (c) => (data += c));
    req.on("end", () => {
      const reply = (code, body) => { res.statusCode = code; res.setHeader("Content-Type", "application/json"); res.end(JSON.stringify(body)); };
      if (req.url === "/token") return mode === "oauth-denied" ? reply(401, { error: "invalid_grant" }) : reply(200, { access_token: "fake-access-token-XYZ", expires_in: 3600 });
      if (req.url?.endsWith("/messages:send")) {
        const body = JSON.parse(data);
        seen.push(body);
        if (mode === "unregistered") return reply(404, { error: { status: "NOT_FOUND", details: [{ errorCode: "UNREGISTERED" }] } });
        return reply(200, { name: "projects/p/messages/1" });
      }
      reply(404, {});
    });
  });
  await new Promise((r) => server.listen(0, "127.0.0.1", r));
  port = server.address().port;
});

after(() => { server.close(); rmSync(dir, { recursive: true, force: true }); });

function serviceAccount(extra = {}) {
  const { privateKey } = generateKeyPairSync("rsa", { modulusLength: 2048 });
  const file = join(dir, `sa-${Math.random().toString(16).slice(2)}.json`);
  const sa = { type: "service_account", project_id: "demo-project", client_email: "svc@demo-project.iam.gserviceaccount.com", private_key: privateKey.export({ type: "pkcs8", format: "pem" }), ...extra };
  writeFileSync(file, JSON.stringify(sa));
  return { file, sa };
}

// Asynchronous on purpose: the fake FCM server lives in THIS process, so the event loop must stay free to answer the child.
function exec(argv) {
  return new Promise((resolve) => {
    execFile(process.execPath, [script, ...argv], { encoding: "utf8", timeout: 30000 }, (error, stdout, stderr) => {
      resolve({ code: error ? (typeof error.code === "number" ? error.code : 1) : 0, out: stdout + stderr });
    });
  });
}
const run = (...args) => exec([...args, "--api-base", `http://127.0.0.1:${port}`, "--oauth-url", `http://127.0.0.1:${port}/token`]);

test("a valid key obtains an access token; nothing secret is printed", async () => {
  mode = "ok";
  const { file, sa } = serviceAccount();
  const r = await run("--service-account", file);
  assert.equal(r.code, 0, r.out);
  assert.match(r.out, /obtained an FCM access token/);
  assert.ok(!r.out.includes("PRIVATE KEY") && !r.out.includes(sa.private_key.slice(40, 80)) && !r.out.includes("fake-access-token-XYZ"), "no key or token in the output");
});

test("with a device token it sends a DRY RUN (validate_only) and delivers nothing", async () => {
  mode = "ok";
  seen.length = 0;
  const { file } = serviceAccount();
  const r = await run("--service-account", file, "--token", "device-token-1234567890123456");
  assert.equal(r.code, 0, r.out);
  assert.match(r.out, /dry run accepted/);
  assert.equal(seen.length, 1);
  assert.equal(seen[0].validate_only, true);
  assert.deepEqual(seen[0].message.data, { type: "push_test" });
});

test("--send really sends (validate_only is false)", async () => {
  mode = "ok";
  seen.length = 0;
  const { file } = serviceAccount();
  const r = await run("--service-account", file, "--token", "device-token-1234567890123456", "--send");
  assert.equal(r.code, 0, r.out);
  assert.equal(seen[0].validate_only, false);
  assert.match(r.out, /accepted by FCM for delivery/);
});

test("a refused credential, an unregistered token and a broken file all fail with a clear reason", async () => {
  const { file } = serviceAccount();
  mode = "oauth-denied";
  let r = await run("--service-account", file);
  assert.equal(r.code, 1);
  assert.match(r.out, /Google refused the credential/);

  mode = "unregistered";
  r = await run("--service-account", file, "--token", "device-token-1234567890123456");
  assert.equal(r.code, 1);
  assert.match(r.out, /not registered for this project/);

  mode = "ok";
  const bad = join(dir, "bad.json");
  writeFileSync(bad, "{ not json");
  assert.equal((await run("--service-account", bad)).code, 1);
  assert.equal((await run("--service-account", join(dir, "missing.json"))).code, 1);
  const { file: incomplete } = serviceAccount({ client_email: "" });
  assert.match((await run("--service-account", incomplete)).out, /no "client_email"/);
  assert.equal((await run()).code, 1);
});

test("endpoint overrides are refused for non-loopback URLs (a credential can't be sent to an outside host)", async () => {
  const { file } = serviceAccount();
  const r = await exec(["--service-account", file, "--oauth-url", "https://evil.example/token"]);
  assert.equal(r.code, 1);
  assert.match(r.out, /only accepted for loopback/);
});
