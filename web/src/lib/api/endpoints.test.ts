// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { createEndpoint, listEndpoints, setEndpointActive } from "@/lib/api/endpoints";

const KEY = "whk_0123456789ab_SECRETSECRETSECRETSECRETSECRETSECRETSECRET";
const ID = "e1e1e1e1-0000-4000-8000-000000000002";

function api(status = 200, body: unknown = {}) {
  const fetchImpl = vi.fn(async () => new Response(JSON.stringify(body), { status }));
  return {
    fetchImpl,
    options: { baseUrl: "http://api.test", apiKey: KEY, fetch: fetchImpl as unknown as typeof fetch },
  };
}

const call = (fetchImpl: ReturnType<typeof api>["fetchImpl"]) => fetchImpl.mock.calls[0] as unknown as [string, RequestInit];

describe("listEndpoints", () => {
  it("fetches the endpoints of the tenant", async () => {
    const { fetchImpl, options } = api(200, [{ id: ID }]);

    expect(await listEndpoints(options)).toEqual({ ok: true, data: [{ id: ID }] });
    expect(call(fetchImpl)[0]).toBe("http://api.test/endpoints");
  });
});

describe("createEndpoint", () => {
  it("registers an address and hands back the signing secret the API shows only once", async () => {
    const created = { id: ID, url: "https://shop.example.com/hooks", isActive: true, createdAt: "x", signingSecret: "whsec_abc" };
    const { fetchImpl, options } = api(201, created);

    const result = await createEndpoint(options, "https://shop.example.com/hooks");

    expect(result).toEqual({ ok: true, data: created });
    const [url, init] = call(fetchImpl);
    expect(url).toBe("http://api.test/endpoints");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body as string)).toEqual({ url: "https://shop.example.com/hooks" });
  });

  it("writes the address as a JSON string, so quotes and line breaks in it cannot change the request", async () => {
    const { fetchImpl, options } = api(201);

    await createEndpoint(options, 'https://x.example/"},"url":"http://evil\n');

    expect(JSON.parse(call(fetchImpl)[1].body as string)).toEqual({ url: 'https://x.example/"},"url":"http://evil\n' });
  });

  it("reports an address the API did not accept", async () => {
    expect(await createEndpoint(api(400).options, "http://10.0.0.1")).toMatchObject({ ok: false, kind: "rejected", status: 400 });
  });
});

describe("setEndpointActive", () => {
  it.each([true, false])("switches an endpoint to isActive %s", async (isActive) => {
    const { fetchImpl, options } = api(200, { id: ID, isActive });

    const result = await setEndpointActive(options, ID, isActive);

    expect(result.ok).toBe(true);
    const [url, init] = call(fetchImpl);
    expect(url).toBe(`http://api.test/endpoints/${ID}`);
    expect(init.method).toBe("PATCH");
    expect(init.body).toBe(JSON.stringify({ isActive }));
  });

  it.each(["", "x", "../me", `${ID}/x`, `x${ID}`])("treats %j as an endpoint that does not exist, without asking the API", async (id) => {
    const { fetchImpl, options } = api();

    expect(await setEndpointActive(options, id, true)).toEqual({ ok: false, kind: "rejected", status: 404 });
    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it("sends only a real true or false", async () => {
    const { fetchImpl, options } = api();

    // A value from a form could be anything; it is only passed on when it is exactly a boolean.
    expect(await setEndpointActive(options, ID, "yes" as unknown as boolean)).toEqual({ ok: false, kind: "rejected", status: 400 });
    expect(fetchImpl).not.toHaveBeenCalled();
  });
});
