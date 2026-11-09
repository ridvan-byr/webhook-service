// @vitest-environment node
import { beforeEach, describe, expect, it, vi } from "vitest";
import { INITIAL_STATE } from "@/lib/events/state";

const mocks = vi.hoisted(() => ({
  requireApi: vi.fn(),
  createEvent: vi.fn(),
  redirect: vi.fn((to: string) => {
    throw new Error(`REDIRECT ${to}`);
  }),
}));

vi.mock("@/lib/session/api-access", () => ({ requireApi: mocks.requireApi }));
vi.mock("@/lib/api/events", () => ({ createEvent: mocks.createEvent }));
vi.mock("next/navigation", () => ({ redirect: mocks.redirect }));

import { sendEvent } from "@/app/(app)/events/new/actions";

const OPTIONS = { baseUrl: "http://api.test", apiKey: "whk_x" };
const EVENT_ID = "c71e04b9-0000-4000-8000-000000000001";

const form = (fields: Record<string, string>) => {
  const data = new FormData();
  for (const [name, value] of Object.entries({ type: "order.created", payload: '{"id": 42}', idempotencyKey: "", ...fields })) {
    data.set(name, value);
  }
  return data;
};

beforeEach(() => {
  vi.clearAllMocks();
  mocks.requireApi.mockResolvedValue(OPTIONS);
});

describe("sendEvent", () => {
  it("checks who is asking itself, before it reads anything", async () => {
    mocks.requireApi.mockImplementation(async () => {
      throw new Error("REDIRECT /session/expired");
    });

    await expect(sendEvent(INITIAL_STATE, form({}))).rejects.toThrow("REDIRECT /session/expired");
    expect(mocks.createEvent).not.toHaveBeenCalled();
  });

  it("sends the event and shows it", async () => {
    mocks.createEvent.mockResolvedValue({ ok: true, data: { eventId: EVENT_ID, deliveries: 2, duplicate: false } });

    await expect(sendEvent(INITIAL_STATE, form({ idempotencyKey: " order-42 " }))).rejects.toThrow(
      `REDIRECT /events/${EVENT_ID}?sent=ok`,
    );
    expect(mocks.createEvent).toHaveBeenCalledWith(OPTIONS, {
      type: "order.created",
      payloadText: '{"id": 42}',
      idempotencyKey: "order-42",
    });
  });

  it("says when the same event had already been accepted", async () => {
    mocks.createEvent.mockResolvedValue({ ok: true, data: { eventId: EVENT_ID, deliveries: 2, duplicate: true } });

    await expect(sendEvent(INITIAL_STATE, form({}))).rejects.toThrow(`REDIRECT /events/${EVENT_ID}?sent=duplicate`);
  });

  it("hands back what is wrong with each field, and what was typed, without asking the API", async () => {
    const state = await sendEvent(INITIAL_STATE, form({ type: " ", payload: "{" }));

    expect(state.errors).toMatchObject({ type: expect.any(String), payload: "The payload is not valid JSON." });
    expect(state.values).toEqual({ type: " ", payload: "{", idempotencyKey: "" });
    expect(mocks.createEvent).not.toHaveBeenCalled();
  });

  it("marks the key when the API says it belongs to a different event", async () => {
    mocks.createEvent.mockResolvedValue({ ok: false, kind: "rejected", status: 409 });

    const state = await sendEvent(INITIAL_STATE, form({ idempotencyKey: "k" }));

    expect(state.errors.idempotencyKey).toMatch(/different event/i);
    expect(state.values.idempotencyKey).toBe("k");
  });

  it.each([
    [{ ok: false, kind: "unreachable" }],
    [{ ok: false, kind: "server", status: 500 }],
    [{ ok: false, kind: "rejected", status: 400 }],
  ])("says it did not work, keeps what was typed and shows nothing the API wrote (%o)", async (result) => {
    mocks.createEvent.mockResolvedValue(result);

    const state = await sendEvent(INITIAL_STATE, form({}));

    expect(state.message).toBe("The event could not be sent. Try again in a moment.");
    expect(state.values.type).toBe("order.created");
  });

  it("makes the visitor sign in again when the key is no longer accepted", async () => {
    mocks.createEvent.mockResolvedValue({ ok: false, kind: "unauthorized", status: 401 });

    await expect(sendEvent(INITIAL_STATE, form({}))).rejects.toThrow("REDIRECT /session/expired");
  });

  it("keeps the session and says so when the API is down", async () => {
    mocks.requireApi.mockResolvedValue(null);

    const state = await sendEvent(INITIAL_STATE, form({}));

    expect(state.message).toMatch(/could not be reached/i);
    expect(mocks.createEvent).not.toHaveBeenCalled();
  });

  it("does not trust an event id it was not given as a real id", async () => {
    mocks.createEvent.mockResolvedValue({ ok: true, data: { eventId: "../login", deliveries: 0, duplicate: false } });

    await expect(sendEvent(INITIAL_STATE, form({}))).rejects.toThrow("REDIRECT /deliveries");
  });

  it("treats missing fields as empty instead of failing", async () => {
    const state = await sendEvent(INITIAL_STATE, new FormData());

    expect(Object.keys(state.errors).sort()).toEqual(["payload", "type"]);
  });
});
