import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Delivery } from "@/lib/api/deliveries";

const mocks = vi.hoisted(() => ({
  requireApi: vi.fn(),
  listDeliveries: vi.fn(),
}));

vi.mock("@/lib/session/api-access", () => ({ requireApi: mocks.requireApi }));
vi.mock("@/lib/api/deliveries", () => ({ listDeliveries: mocks.listDeliveries }));
vi.mock("next/navigation", () => ({
  useRouter: () => ({ refresh: vi.fn() }),
  redirect: (to: string) => {
    throw new Error(`REDIRECT ${to}`);
  },
  notFound: () => {
    throw new Error("NOT_FOUND");
  },
}));

import DeliveriesPage from "@/app/(app)/deliveries/page";

const OPTIONS = { baseUrl: "http://api.test", apiKey: "whk_x" };
const delivery = (): Delivery => ({
  id: "3f2a91c0-1b2d-4e5f-8a9b-0c1d2e3f4a5b",
  eventId: "e",
  eventType: "order.created",
  endpointId: "p",
  endpointUrl: "https://shop.example.com/hooks",
  status: "Dead",
  attemptCount: 8,
  nextAttemptAt: "2026-01-05T09:00:00+00:00",
  lastError: null,
  createdAt: "2026-01-05T08:00:00+00:00",
});

const show = async (status?: string) =>
  render(await DeliveriesPage({ searchParams: Promise.resolve(status ? { status } : {}) } as never));

beforeEach(() => {
  vi.resetAllMocks();
  mocks.requireApi.mockResolvedValue(OPTIONS);
});

describe("deliveries page", () => {
  it("lists the deliveries under one level 1 heading, with the filter and automatic refresh", async () => {
    mocks.listDeliveries.mockResolvedValue({ ok: true, data: [delivery()] });

    await show();

    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    expect(screen.getByRole("table", { name: "Deliveries" })).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Filter by status" })).toBeInTheDocument();
  });

  it("asks the API with the key of the signed-in user and the status from the address", async () => {
    mocks.listDeliveries.mockResolvedValue({ ok: true, data: [] });

    await show("Dead");

    expect(mocks.listDeliveries).toHaveBeenCalledWith(OPTIONS, "Dead");
  });

  it("invites the visitor to send an event when there are no deliveries at all", async () => {
    mocks.listDeliveries.mockResolvedValue({ ok: true, data: [] });

    await show();

    expect(screen.getByRole("link", { name: /send a test event/i })).toHaveAttribute("href", "/events/new");
    expect(screen.queryByRole("table")).toBeNull();
  });

  it("says when a filter matches nothing, without sending the visitor away", async () => {
    mocks.listDeliveries.mockResolvedValue({ ok: true, data: [] });

    await show("Dead");

    expect(screen.getByText(/no dead deliveries/i)).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Filter by status" })).toBeInTheDocument();
  });

  it("makes the visitor sign in again when the API no longer accepts the key", async () => {
    mocks.listDeliveries.mockResolvedValue({ ok: false, kind: "unauthorized", status: 401 });

    await expect(show()).rejects.toThrow("REDIRECT /session/expired");
  });

  it("shows a message, not a broken page, when the API fails", async () => {
    mocks.listDeliveries.mockResolvedValue({ ok: false, kind: "unreachable" });

    await show();

    expect(screen.getByRole("alert")).toHaveTextContent(/could not be reached|did not answer/i);
  });

  it("shows the same message when the API is down before the list is even asked for", async () => {
    mocks.requireApi.mockResolvedValue(null);

    await show();

    expect(screen.getByRole("alert")).toBeInTheDocument();
    expect(mocks.listDeliveries).not.toHaveBeenCalled();
  });
});
