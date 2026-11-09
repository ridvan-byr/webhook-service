// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { apiGet, apiPost } from "@/lib/api/client";

const BASE = "http://api.test:5005";
const KEY = "whk_0123456789ab_SECRETSECRETSECRETSECRETSECRETSECRETSECRET";

function reply(status: number, body: unknown = {}, raw = false) {
  return vi.fn(async () => new Response(raw ? (body as string) : JSON.stringify(body), { status }));
}

function call<T = unknown>(fetchImpl: typeof fetch, path = "/me", timeoutMs = 1000) {
  return apiGet<T>(path, { baseUrl: BASE, apiKey: KEY, fetch: fetchImpl, timeoutMs });
}

describe("apiPost", () => {
  const post = (fetchImpl: typeof fetch, path = "/deliveries/1/resend") =>
    apiPost(path, { baseUrl: BASE, apiKey: KEY, fetch: fetchImpl });

  it("sends a POST for the signed-in user, never cached and never following a redirect", async () => {
    const fetchImpl = reply(202, { status: "Pending" });

    const result = await post(fetchImpl as unknown as typeof fetch);

    expect(result).toEqual({ ok: true, data: { status: "Pending" } });
    const [url, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("http://api.test:5005/deliveries/1/resend");
    expect(init.method).toBe("POST");
    expect(init.cache).toBe("no-store");
    expect(init.redirect).toBe("manual");
    expect((init.headers as Record<string, string>).Authorization).toBe(`Bearer ${KEY}`);
  });

  it("tells the API's answers apart in the same way as a read does", async () => {
    expect(await post(reply(409) as unknown as typeof fetch)).toMatchObject({ ok: false, kind: "rejected", status: 409 });
    expect(await post(reply(401) as unknown as typeof fetch)).toMatchObject({ ok: false, kind: "unauthorized" });
    expect(await post(reply(500) as unknown as typeof fetch)).toMatchObject({ ok: false, kind: "server" });
  });

  it("refuses a path that could lead somewhere else", async () => {
    const fetchImpl = reply(200);

    await expect(post(fetchImpl as unknown as typeof fetch, "//evil.example")).rejects.toThrow(/path/i);
    expect(fetchImpl).not.toHaveBeenCalled();
  });
});

describe("apiGet", () => {
  it("asks the API on behalf of the signed-in user and returns what it answers", async () => {
    const fetchImpl = reply(200, { tenantName: "Acme" });

    const result = await call(fetchImpl as unknown as typeof fetch);

    expect(result).toEqual({ ok: true, data: { tenantName: "Acme" } });
    const [url, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("http://api.test:5005/me");
    expect(init.method).toBe("GET");
    expect((init.headers as Record<string, string>).Authorization).toBe(`Bearer ${KEY}`);
  });

  it("never lets a response be stored or reused: it belongs to one user", async () => {
    const fetchImpl = reply(200);

    await call(fetchImpl as unknown as typeof fetch);

    const init = (fetchImpl.mock.calls[0] as unknown as [string, RequestInit])[1];
    expect(init.cache).toBe("no-store");
  });

  it("does not follow redirects, so the key cannot be taken to another address", async () => {
    const fetchImpl = reply(302, {});

    const result = await call(fetchImpl as unknown as typeof fetch);

    expect((fetchImpl.mock.calls[0] as unknown as [string, RequestInit])[1].redirect).toBe("manual");
    expect(result).toMatchObject({ ok: false, kind: "server" });
  });

  it.each([
    [401, "unauthorized"],
    [403, "rejected"],
    [404, "rejected"],
    [409, "rejected"],
    [400, "rejected"],
    [500, "server"],
    [502, "server"],
    [503, "server"],
  ])("tells a %i from the API apart as %s", async (status, kind) => {
    const result = await call(reply(status) as unknown as typeof fetch);

    expect(result).toMatchObject({ ok: false, kind, status });
  });

  it("reports an API that cannot be reached", async () => {
    const fetchImpl = vi.fn(async () => {
      throw new TypeError("fetch failed");
    });

    expect(await call(fetchImpl as unknown as typeof fetch)).toMatchObject({ ok: false, kind: "unreachable" });
  });

  it("gives up when the API does not answer in time", async () => {
    const hangs = vi.fn(
      (_url: string, init: RequestInit) =>
        new Promise<Response>((_resolve, reject) => {
          init.signal?.addEventListener("abort", () => reject(new DOMException("aborted", "TimeoutError")));
        }),
    );

    const result = await call(hangs as unknown as typeof fetch, "/me", 30);

    expect(result).toMatchObject({ ok: false, kind: "unreachable" });
  });

  it("treats an answer that is not JSON as a fault of the API", async () => {
    const result = await call(reply(200, "<html>oops</html>", true) as unknown as typeof fetch);

    expect(result).toMatchObject({ ok: false, kind: "server" });
  });

  it("never puts the key or the API's own words into the result, whatever went wrong", async () => {
    const leaky = reply(500, `internal detail ${KEY} stack trace at Foo.Bar`, true);
    const unreachable = vi.fn(async () => {
      throw new Error(`connect ECONNREFUSED while sending ${KEY}`);
    });

    for (const fetchImpl of [leaky, unreachable]) {
      const result = await call(fetchImpl as unknown as typeof fetch);

      const text = JSON.stringify(result);
      expect(text).not.toContain(KEY);
      expect(text).not.toContain("whk_");
      expect(text).not.toContain("stack trace");
      expect(text).not.toContain("ECONNREFUSED");
    }
  });

  it.each([
    ["without a leading slash", "me"],
    ["that starts a second host", "//evil.example/x"],
    ["that is a whole address", "http://evil.example/x"],
    ["that tries to become part of the host name", "@evil.example"],
    ["with a line break", "/me\r\nX-Injected: 1"],
  ])("refuses a path %s, so the request can only go to the API", async (_why, path) => {
    const fetchImpl = reply(200);

    await expect(call(fetchImpl as unknown as typeof fetch, path)).rejects.toThrow(/path/i);
    expect(fetchImpl).not.toHaveBeenCalled();
  });
});
