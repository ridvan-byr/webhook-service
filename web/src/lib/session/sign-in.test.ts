// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { openSession } from "@/lib/session/crypto";
import { performSignIn } from "@/lib/session/sign-in";

const SECRET = "0123456789abcdef0123456789abcdef";
const KEY = "whk_0123456789ab_SECRETSECRETSECRETSECRETSECRETSECRETSECRET";
const NOW = new Date("2026-01-01T00:00:00Z");

function setup(status: number | "network", production = true) {
  const fetchImpl = vi.fn(async () => {
    if (status === "network") throw new TypeError("fetch failed");
    return new Response(JSON.stringify({ tenantName: "Acme" }), { status });
  });
  const setCookie = vi.fn();
  const run = (apiKey: string) =>
    performSignIn(apiKey, {
      baseUrl: "http://api.test",
      secret: SECRET,
      now: NOW,
      production,
      fetch: fetchImpl as unknown as typeof fetch,
      setCookie,
    });
  return { run, fetchImpl, setCookie };
}

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
