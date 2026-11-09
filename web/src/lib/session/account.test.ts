// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { loadAccount } from "@/lib/session/account";
import { SESSION_LIFETIME_SECONDS } from "@/lib/session/cookie";
import { sealSession } from "@/lib/session/crypto";

const SECRET = "0123456789abcdef0123456789abcdef";
const KEY = "whk_0123456789ab_SECRETSECRETSECRETSECRETSECRETSECRETSECRET";
const NOW = new Date("2026-01-01T00:00:00Z");

const token = (now = NOW, secret = SECRET) =>
  sealSession({ apiKey: KEY }, { secret, now, lifetimeSeconds: SESSION_LIFETIME_SECONDS });

function run(cookie: string | undefined, status: number | "network" = 200) {
  const fetchImpl = vi.fn(async () => {
    if (status === "network") throw new TypeError("fetch failed");
    return new Response(
      JSON.stringify({ tenantId: "t1", tenantName: "Acme", apiKeyId: "k1", apiKeyName: "ci-deploy" }),
      { status },
    );
  });
  const result = loadAccount(cookie, {
    secret: SECRET,
    now: NOW,
    baseUrl: "http://api.test",
    fetch: fetchImpl as unknown as typeof fetch,
  });
  return { result, fetchImpl };
}

describe("loadAccount", () => {
  it("asks the API who the key belongs to, so a revoked key is noticed on every page", async () => {
    const { result, fetchImpl } = run(token());

    expect(await result).toEqual({
      status: "ok",
      apiKey: KEY,
      account: { tenantName: "Acme", keyName: "ci-deploy" },
    });
    expect(fetchImpl).toHaveBeenCalledTimes(1);
  });

  it.each([
    ["no cookie", undefined],
    ["a cookie that is not a session", "garbage"],
    ["a session made with another secret", token(NOW, "another-secret-another-secret-12345")],
    ["a session that has run out", token(new Date(NOW.getTime() - (SESSION_LIFETIME_SECONDS + 1) * 1000))],
  ])("treats %s as signed out, without bothering the API", async (_why, cookie) => {
    const { result, fetchImpl } = run(cookie);

    expect(await result).toEqual({ status: "signed-out" });
    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it("treats a key the API no longer accepts as signed out", async () => {
    expect(await run(token(), 401).result).toEqual({ status: "signed-out" });
  });

  it.each([500, "network"] as const)("keeps the session when the API is only down (%s)", async (status) => {
    // Signing out because of an outage would throw away a session that is still good.
    expect(await run(token(), status).result).toEqual({ status: "api-down" });
  });

  it("never returns the key inside the account that pages show", async () => {
    const result = await run(token()).result;

    expect(result.status === "ok" && JSON.stringify(result.account)).not.toContain("whk_");
  });
});
