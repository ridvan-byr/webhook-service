import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { EventDetail } from "@/lib/api/events";

const mocks = vi.hoisted(() => ({ requireApi: vi.fn(), getEvent: vi.fn() }));

vi.mock("@/lib/session/api-access", () => ({ requireApi: mocks.requireApi }));
vi.mock("@/lib/api/events", () => ({ getEvent: mocks.getEvent }));
vi.mock("next/navigation", () => ({
  useRouter: () => ({ refresh: vi.fn() }),
  redirect: (to: string) => {
    throw new Error(`REDIRECT ${to}`);
  },
  notFound: () => {
    throw new Error("NOT_FOUND");
  },
}));

import EventPage from "@/app/(app)/events/[id]/page";

const ID = "c71e04b9-0000-4000-8000-000000000001";
const event = (overrides: Partial<EventDetail> = {}): EventDetail => ({
  id: ID,
  type: "order.created",
  createdAt: "2026-01-05T08:00:00+00:00",
  payload: { id: 42, items: ["a", "b"] },
  deliveries: [
    {
      id: "3f2a91c0-1b2d-4e5f-8a9b-0c1d2e3f4a5b",
      eventId: ID,
      eventType: "order.created",
      endpointId: "p",
      endpointUrl: "https://shop.example.com/hooks",
      status: "Succeeded",
      attemptCount: 1,
      nextAttemptAt: "2026-01-05T08:00:00+00:00",
      lastError: null,
      createdAt: "2026-01-05T08:00:00+00:00",
    },
  ],
  ...overrides,
});

const show = async (sent?: string) =>
  render(
    await EventPage({
      params: Promise.resolve({ id: ID }),
      searchParams: Promise.resolve(sent ? { sent } : {}),
    } as never),
  );

beforeEach(() => {
  vi.resetAllMocks();
  mocks.requireApi.mockResolvedValue({ baseUrl: "http://api.test", apiKey: "whk_x" });
});

describe("event page", () => {
  it("shows the event, its payload and what became of it, under one level 1 heading", async () => {
    mocks.getEvent.mockResolvedValue({ ok: true, data: event() });

    await show();

    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("order.created");
    expect(screen.getByText("Received 2026-01-05 08:00:00 UTC")).toBeInTheDocument();
    expect(screen.getByRole("table", { name: "Deliveries" })).toBeInTheDocument();
  });

  it("asks the API for the event named in the address", async () => {
    mocks.getEvent.mockResolvedValue({ ok: true, data: event() });

    await show();

    expect(mocks.getEvent).toHaveBeenCalledWith({ baseUrl: "http://api.test", apiKey: "whk_x" }, ID);
  });

  it("shows the payload indented, so it can be read", async () => {
    mocks.getEvent.mockResolvedValue({ ok: true, data: event() });

    await show();

    const payload = screen.getByLabelText("Payload");
    expect(payload.tagName).toBe("PRE");
    expect(payload.textContent).toBe(JSON.stringify({ id: 42, items: ["a", "b"] }, null, 2));
  });

  it("shows a payload as text, never as markup", async () => {
    mocks.getEvent.mockResolvedValue({ ok: true, data: event({ payload: { note: "<img src=x onerror=alert(1)>" } }) });

    const { container } = await show();

    expect(container.querySelector("img")).toBeNull();
    expect(screen.getByLabelText("Payload").textContent).toContain("<img src=x onerror=alert(1)>");
  });

  it("says why there is nothing in the table when no endpoint took the event", async () => {
    mocks.getEvent.mockResolvedValue({ ok: true, data: event({ deliveries: [] }) });

    await show();

    expect(screen.queryByRole("table")).toBeNull();
    expect(screen.getByText(/no active endpoint/i)).toBeInTheDocument();
  });

  it("links back to the list of deliveries", async () => {
    mocks.getEvent.mockResolvedValue({ ok: true, data: event() });

    await show();

    expect(screen.getByRole("link", { name: /all deliveries/i })).toHaveAttribute("href", "/deliveries");
  });

  it("tells that the event was accepted and how many deliveries it made", async () => {
    mocks.getEvent.mockResolvedValue({ ok: true, data: event() });

    await show("ok");

    expect(screen.getByRole("status")).toHaveTextContent(/accepted/i);
  });

  it("tells that nothing new was made when the same event was sent again", async () => {
    mocks.getEvent.mockResolvedValue({ ok: true, data: event() });

    await show("duplicate");

    expect(screen.getByRole("status")).toHaveTextContent(/already/i);
  });

  it("ignores any other value in the address instead of printing it", async () => {
    mocks.getEvent.mockResolvedValue({ ok: true, data: event() });

    const { container } = await show("<b>hi</b>");

    expect(container.querySelector("b")).toBeNull();
    expect(screen.queryByRole("status")).toBeNull();
  });

  it.each(["constructor", "toString", "__proto__", "hasOwnProperty"])(
    "does not mistake %s in the address for a note it knows",
    async (sent) => {
      // These are names every JavaScript object has. Looking them up in a plain object would find something.
      mocks.getEvent.mockResolvedValue({ ok: true, data: event() });

      await show(sent);

      expect(screen.queryByRole("status")).toBeNull();
    },
  );

  it("says the page does not exist when the API does not know the event", async () => {
    mocks.getEvent.mockResolvedValue({ ok: false, kind: "rejected", status: 404 });

    await expect(show()).rejects.toThrow("NOT_FOUND");
  });

  it("makes the visitor sign in again when the key is no longer accepted", async () => {
    mocks.getEvent.mockResolvedValue({ ok: false, kind: "unauthorized", status: 401 });

    await expect(show()).rejects.toThrow("REDIRECT /session/expired");
  });

  it("shows a message when the API fails or is down", async () => {
    mocks.getEvent.mockResolvedValue({ ok: false, kind: "server", status: 500 });
    await show();
    expect(screen.getByRole("alert")).toBeInTheDocument();
  });

  it("shows the same message without asking when the API is down before the event is requested", async () => {
    mocks.requireApi.mockResolvedValue(null);

    await show();

    expect(screen.getByRole("alert")).toBeInTheDocument();
    expect(mocks.getEvent).not.toHaveBeenCalled();
  });
});
