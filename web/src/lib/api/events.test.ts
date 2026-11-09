// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { createEvent, getEvent } from "@/lib/api/events";

const KEY = "whk_0123456789ab_SECRETSECRETSECRETSECRETSECRETSECRETSECRET";
const ID = "c71e04b9-0000-4000-8000-000000000001";

function api(status = 200, body: unknown = {}) {
  const fetchImpl = vi.fn(async () => new Response(JSON.stringify(body), { status }));
  return {
    fetchImpl,
    options: { baseUrl: "http://api.test", apiKey: KEY, fetch: fetchImpl as unknown as typeof fetch },
  };
}

const call = (fetchImpl: ReturnType<typeof api>["fetchImpl"]) => fetchImpl.mock.calls[0] as unknown as [string, RequestInit];

describe("getEvent", () => {
  it("fetches an event with its deliveries", async () => {
    const { fetchImpl, options } = api(200, { id: ID, type: "a", payload: {}, deliveries: [] });

    const result = await getEvent(options, ID);

    expect(result.ok).toBe(true);
    expect(call(fetchImpl)[0]).toBe(`http://api.test/events/${ID}`);
  });

  it.each(["", "x", "../me", `${ID}/x`, `x${ID}`])("treats %j as an event that does not exist, without asking the API", async (id) => {
    const { fetchImpl, options } = api();

    expect(await getEvent(options, id)).toEqual({ ok: false, kind: "rejected", status: 404 });
    expect(fetchImpl).not.toHaveBeenCalled();
  });
});

describe("createEvent", () => {
  it("sends the payload exactly as it was written, so the signature covers the same characters", async () => {
    const { fetchImpl, options } = api(202, { eventId: ID, deliveries: 1, duplicate: false });
    const payloadText = '{\n  "id":   42,\n  "big": 12345678901234567890\n}';

    const result = await createEvent(options, { type: "order.created", payloadText, idempotencyKey: undefined });

    expect(result).toEqual({ ok: true, data: { eventId: ID, deliveries: 1, duplicate: false } });
    const [url, init] = call(fetchImpl);
    expect(url).toBe("http://api.test/events");
    expect(init.method).toBe("POST");
    expect(init.body).toBe(`{"type":"order.created","payload":${payloadText}}`);
    expect((init.headers as Record<string, string>)["Content-Type"]).toBe("application/json");
  });

  it("sends the idempotency key when there is one", async () => {
    const { fetchImpl, options } = api(202);

    await createEvent(options, { type: "a", payloadText: "{}", idempotencyKey: "order-42" });

    expect(JSON.parse(call(fetchImpl)[1].body as string)).toEqual({ type: "a", payload: {}, idempotencyKey: "order-42" });
  });

  it("writes the type and the key as JSON strings, so quotes and line breaks in them cannot change the request", async () => {
    const { fetchImpl, options } = api(202);

    await createEvent(options, { type: 'a","payload":{"evil":1},"x":"', payloadText: "{}", idempotencyKey: 'k"\n' });

    expect(JSON.parse(call(fetchImpl)[1].body as string)).toEqual({
      type: 'a","payload":{"evil":1},"x":"',
      payload: {},
      idempotencyKey: 'k"\n',
    });
  });

  it.each(['1,"type":"evil"', "{", "", "null"])(
    "does not send a payload that is not one JSON value (%j), whatever the caller checked before",
    async (payloadText) => {
      const { fetchImpl, options } = api();

      expect(await createEvent(options, { type: "a", payloadText, idempotencyKey: undefined })).toEqual({
        ok: false,
        kind: "rejected",
        status: 400,
      });
      expect(fetchImpl).not.toHaveBeenCalled();
    },
  );

  it("reports that the key was used for another event", async () => {
    expect(await createEvent(api(409).options, { type: "a", payloadText: "{}", idempotencyKey: "k" })).toMatchObject({
      ok: false,
      kind: "rejected",
      status: 409,
    });
  });

  it("passes on that an identical event was already accepted", async () => {
    const { options } = api(200, { eventId: ID, deliveries: 2, duplicate: true });

    expect(await createEvent(options, { type: "a", payloadText: "{}", idempotencyKey: "k" })).toEqual({
      ok: true,
      data: { eventId: ID, deliveries: 2, duplicate: true },
    });
  });
});
