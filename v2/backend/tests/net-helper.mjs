// Test-infrastructure resilience: the suites talk to a local Supabase stack, and on a busy machine a SETUP request can fail
// at the transport level ("fetch failed": connect/header timeout) before it reaches the server. That used to fail every test
// in the file through the `before` hook. This wrapper retries only calls that are safe to repeat and only when the request
// threw without any HTTP answer:
//   * GET / HEAD requests, and
//   * password sign-in (POST /auth/v1/token), which is idempotent.
// It never retries the Edge Function calls under test, never retries anything that received a response, and so cannot hide
// a wrong status code or a wrong body.

const original = globalThis.fetch;

function safeToRepeat(input, init) {
  const method = String(init?.method ?? (typeof input === "object" && input?.method) ?? "GET").toUpperCase();
  const url = String(typeof input === "string" || input instanceof URL ? input : input?.url);
  return method === "GET" || method === "HEAD" || (method === "POST" && /\/auth\/v1\/token\?grant_type=password/.test(url));
}

globalThis.fetch = async function resilientFetch(input, init) {
  const attempts = safeToRepeat(input, init) ? 4 : 1;
  for (let attempt = 1; ; attempt++) {
    try {
      return await original(input, init);
    } catch (error) {
      if (attempt >= attempts) throw error;
      await new Promise((resolve) => setTimeout(resolve, 500 * attempt));
    }
  }
};

/** `npx supabase status` (used to fetch the service key) retried a few times: it can fail while Docker is busy. */
export function retrySync(fn, attempts = 4) {
  for (let attempt = 1; ; attempt++) {
    try {
      return fn();
    } catch (error) {
      if (attempt >= attempts) throw error;
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 1000 * attempt);
    }
  }
}
