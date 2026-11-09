// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { openSession } from "@/lib/session/crypto";
import { performSignIn } from "@/lib/session/sign-in";
import { SignInLimiter } from "@/lib/session/sign-in-limiter";

const SECRET = "0123456789abcdef0123456789abcdef";
const KEY = "whk_0123456789ab_SECRETSECRETSECRETSECRETSECRETSECRETSECRET";
const NOW = new Date("2026-01-01T00:00:00Z");

// A limit far above what the older tests do, so they are about sign-in and not about the limit.
const roomy = () => new SignInLimiter({ maxFailures: 1000, windowMs: 60_000 });

function setup(status: number | "network", production = true, limiter: SignInLimiter = roomy()) {
  const fetchImpl = vi.fn(async () => {
    if (status === "network") throw new TypeError("fetch failed");
    return new Response(JSON.stringify({ tenantName: "Acme" }), { status });
  });
  const setCookie = vi.fn();
  const onLimitReached = vi.fn();
  const run = (apiKey: string) =>
    performSignIn(apiKey, {
      baseUrl: "http://api.test",
      secret: SECRET,
      now: NOW,
      production,
      fetch: fetchImpl as unknown as typeof fetch,
      setCookie,
      limiter,
      onLimitReached,
    });
  return { run, fetchImpl, setCookie, onLimitReached };
}

describe("performSignIn: too many failed attempts", () => {
  const tight = (maxFailures = 3) => new SignInLimiter({ maxFailures, windowMs: 60_000 });

  it("stops asking the API once too many keys were turned down, even for a key that would work", async () => {
    const limiter = tight(3);
    const rejected = setup(401, true, limiter);
    for (let i = 0; i < 3; i++) await rejected.run(KEY);

    const afterwards = setup(200, true, limiter);
    const result = await afterwards.run(KEY);

    expect(result).toEqual({ ok: false, message: "Too many failed attempts. Try again in 60 seconds." });
    expect(afterwards.fetchImpl).not.toHaveBeenCalled();
    expect(afterwards.setCookie).not.toHaveBeenCalled();
  });

  it("does not block before the limit", async () => {
    const limiter = tight(3);
    const rejected = setup(401, true, limiter);
    await rejected.run(KEY);
    await rejected.run(KEY);

    const result = await setup(200, true, limiter).run(KEY);

    expect(result).toEqual({ ok: true });
  });

  it("says seconds in the singular for one", async () => {
    let now = 0;
    const limiter = new SignInLimiter({ maxFailures: 1, windowMs: 60_000, now: () => now });
    await setup(401, true, limiter).run(KEY);
    now = 59_500;

    expect(await setup(200, true, limiter).run(KEY)).toEqual({
      ok: false,
      message: "Too many failed attempts. Try again in 1 second.",
    });
  });

  it("counts only keys the API turned down, not its being unreachable or failing", async () => {
    const limiter = tight(2);

    for (let i = 0; i < 10; i++) await setup("network", true, limiter).run(KEY);
    for (let i = 0; i < 10; i++) await setup(500, true, limiter).run(KEY);

    expect(await setup(200, true, limiter).run(KEY)).toEqual({ ok: true });
  });

  it("counts a key the API rejected with 403 or 404 as well", async () => {
    const limiter = tight(2);
    await setup(403, true, limiter).run(KEY);
    await setup(404, true, limiter).run(KEY);

    expect(await setup(200, true, limiter).run(KEY)).toMatchObject({ ok: false });
  });

  it("does not call a 429 from the API a wrong key: the API refused for a while, and that is not the visitor's mistake", async () => {
    const limiter = tight(2);

    for (let i = 0; i < 5; i++) {
      const result = await setup(429, true, limiter).run(KEY);
      expect(result).toEqual({ ok: false, message: "The API is not accepting sign-ins right now. Try again in a minute." });
    }

    // Nothing was counted against the visitor.
    expect(await setup(200, true, limiter).run(KEY)).toEqual({ ok: true });
  });

  it("does not count an empty key, which asks the API nothing", async () => {
    const limiter = tight(2);

    for (let i = 0; i < 10; i++) await setup(401, true, limiter).run("   ");

    expect(await setup(200, true, limiter).run(KEY)).toEqual({ ok: true });
  });

  it("does not count a sign-in that worked", async () => {
    const limiter = tight(2);

    for (let i = 0; i < 10; i++) await setup(200, true, limiter).run(KEY);

    expect(await setup(401, true, limiter).run(KEY)).toMatchObject({ ok: false, message: "That API key was not accepted." });
  });

  it("reports once, when the limit is reached, so the operator can see it in the log", async () => {
    const limiter = tight(2);
    const first = setup(401, true, limiter);

    await first.run(KEY);
    expect(first.onLimitReached).not.toHaveBeenCalled();
    await first.run(KEY);
    expect(first.onLimitReached).toHaveBeenCalledTimes(1);
    await first.run(KEY);

    expect(first.onLimitReached).toHaveBeenCalledTimes(1);
  });
});

describe("performSignIn", () => {
  it("lets a key the API accepts in, and keeps it only inside an encrypted cookie", async () => {
    const { run, setCookie } = setup(200);

    const result = await run(KEY);

    expect(result).toEqual({ ok: true });
    expect(setCookie).toHaveBeenCalledTimes(1);
    const [name, value, options] = setCookie.mock.calls[0];
    expect(name).toBe("__Host-session");
    expect(value).not.toContain(KEY);
    expect(options).toMatchObject({ httpOnly: true, sameSite: "strict", secure: true, path: "/" });
    expect(openSession(value, { secret: SECRET, now: NOW })).toEqual({ apiKey: KEY });
  });

  it("ignores spaces that came along when the key was pasted", async () => {
    const { run, fetchImpl, setCookie } = setup(200);

    await run(`  ${KEY}\n`);

    const init = (fetchImpl.mock.calls[0] as unknown as [string, RequestInit])[1];
    expect((init.headers as Record<string, string>).Authorization).toBe(`Bearer ${KEY}`);
    expect(openSession(setCookie.mock.calls[0][1], { secret: SECRET, now: NOW })).toEqual({ apiKey: KEY });
  });

  it.each([401, 403, 404])("gives the same answer for every key the API turns down (%i)", async (status) => {
    const { run, setCookie } = setup(status);

    const result = await run(KEY);

    expect(result).toEqual({ ok: false, message: "That API key was not accepted." });
    expect(setCookie).not.toHaveBeenCalled();
  });

  it("does not even ask the API about an empty key", async () => {
    const { run, fetchImpl, setCookie } = setup(200);

    const result = await run("   ");

    expect(result).toEqual({ ok: false, message: "That API key was not accepted." });
    expect(fetchImpl).not.toHaveBeenCalled();
    expect(setCookie).not.toHaveBeenCalled();
  });

  it.each([
    ["cannot be reached", "network"],
    ["fails", 500],
  ] as const)("says the API is the problem, not the key, when it %s", async (_why, status) => {
    const { run, setCookie } = setup(status);

    const result = await run(KEY);

    expect(result).toEqual({ ok: false, message: "The API could not be reached. Try again in a moment." });
    expect(setCookie).not.toHaveBeenCalled();
  });

  it("never repeats the key in a message", async () => {
    for (const status of [401, 500, "network"] as const) {
      const { run } = setup(status);
      expect(JSON.stringify(await run(KEY))).not.toContain("whk_");
    }
  });

  it("uses a cookie that works over plain http while developing", async () => {
    const { run, setCookie } = setup(200, false);

    await run(KEY);

    expect(setCookie.mock.calls[0][0]).toBe("session");
    expect(setCookie.mock.calls[0][2]).toMatchObject({ secure: false });
  });
});
