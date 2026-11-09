// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { STATUSES, getDelivery, listDeliveries, resendDelivery } from "@/lib/api/deliveries";

const KEY = "whk_0123456789ab_SECRETSECRETSECRETSECRETSECRETSECRETSECRET";
const ID = "3f2a91c0-1b2d-4e5f-8a9b-0c1d2e3f4a5b";

function api(status = 200, body: unknown = []) {
  const fetchImpl = vi.fn(async () => new Response(JSON.stringify(body), { status }));
  return {
    fetchImpl,
    options: { baseUrl: "http://api.test", apiKey: KEY, fetch: fetchImpl as unknown as typeof fetch },
  };
}

const urlOf = (fetchImpl: ReturnType<typeof api>["fetchImpl"]) =>
  (fetchImpl.mock.calls[0] as unknown as [string, RequestInit])[0];

describe("listDeliveries", () => {
  it("asks for the newest deliveries, up to a fixed number", async () => {
    const { fetchImpl, options } = api(200, [{ id: ID }]);

    const result = await listDeliveries(options);

    expect(result).toEqual({ ok: true, data: [{ id: ID }] });
    expect(urlOf(fetchImpl)).toBe("http://api.test/deliveries?limit=100");
  });

  it.each(STATUSES)("narrows the list to %s when asked", async (status) => {
    const { fetchImpl, options } = api();

    await listDeliveries(options, status);

    expect(urlOf(fetchImpl)).toBe(`http://api.test/deliveries?limit=100&status=${status}`);
  });

  it.each(["", "all", "dead", "Dead&limit=1", "../x", "Pending\r\nX: 1", undefined])(
    "ignores a status it does not know (%j), so the address cannot be shaped from outside",
    async (status) => {
      const { fetchImpl, options } = api();

      await listDeliveries(options, status);

      expect(urlOf(fetchImpl)).toBe("http://api.test/deliveries?limit=100");
    },
  );
});

describe("getDelivery", () => {
  it("fetches one delivery with its attempts", async () => {
    const { fetchImpl, options } = api(200, { delivery: { id: ID }, attempts: [] });

    const result = await getDelivery(options, ID);

    expect(result).toEqual({ ok: true, data: { delivery: { id: ID }, attempts: [] } });
    expect(urlOf(fetchImpl)).toBe(`http://api.test/deliveries/${ID}`);
  });

  it.each(["", "1", "not-a-guid", `${ID}/resend`, "../me", `${ID}?x=1`, `../${ID}`, `x${ID}`, "3F2A91C0-1B2D-4E5F-8A9B-0C1D2E3F4A5B\n"])(
    "treats %j as a delivery that does not exist, without asking the API",
    async (id) => {
      const { fetchImpl, options } = api();

      expect(await getDelivery(options, id)).toEqual({ ok: false, kind: "rejected", status: 404 });
      expect(fetchImpl).not.toHaveBeenCalled();
    },
  );
});

describe("resendDelivery", () => {
  it("asks the API to send a dead delivery again", async () => {
    const { fetchImpl, options } = api(202, { id: ID, status: "Pending" });

    const result = await resendDelivery(options, ID);

    expect(result.ok).toBe(true);
    const [url, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe(`http://api.test/deliveries/${ID}/resend`);
    expect(init.method).toBe("POST");
  });

  it("reports that a delivery that is not dead cannot be sent again", async () => {
    expect(await resendDelivery(api(409).options, ID)).toMatchObject({ ok: false, kind: "rejected", status: 409 });
  });

  it("does not ask the API about an id that is not a delivery id", async () => {
    const { fetchImpl, options } = api();

    expect(await resendDelivery(options, "../x")).toEqual({ ok: false, kind: "rejected", status: 404 });
    expect(fetchImpl).not.toHaveBeenCalled();
  });
});
