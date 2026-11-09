import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DeliveryDetail } from "@/lib/api/deliveries";

const mocks = vi.hoisted(() => ({
  requireApi: vi.fn(),
  getDelivery: vi.fn(),
  resend: vi.fn(),
}));

vi.mock("@/lib/session/api-access", () => ({ requireApi: mocks.requireApi }));
vi.mock("@/lib/api/deliveries", () => ({ getDelivery: mocks.getDelivery }));
vi.mock("@/app/(app)/deliveries/[id]/actions", () => ({ resend: mocks.resend }));
vi.mock("next/navigation", () => ({
  useRouter: () => ({ refresh: vi.fn() }),
  redirect: (to: string) => {
    throw new Error(`REDIRECT ${to}`);
  },
  notFound: () => {
    throw new Error("NOT_FOUND");
  },
}));

import DeliveryPage from "@/app/(app)/deliveries/[id]/page";

const ID = "3f2a91c0-1b2d-4e5f-8a9b-0c1d2e3f4a5b";
const detail = (status = "Dead"): DeliveryDetail => ({
  delivery: {
    id: ID,
    eventId: "c71e04b9-0000-4000-8000-000000000001",
    eventType: "order.created",
    endpointId: "p",
    endpointUrl: "https://shop.example.com/hooks",
    status,
    attemptCount: 2,
    nextAttemptAt: "2026-01-05T09:00:00+00:00",
    lastError: "HTTP 500",
    createdAt: "2026-01-05T08:00:00+00:00",
  },
  attempts: [
    {
      id: "a1",
      startedAt: "2026-01-05T08:00:01+00:00",
      durationMs: 120,
      responseStatus: 500,
      responseSnippet: "boom",
      error: null,
    },
  ],
});

const show = async (resendOutcome?: string) =>
  render(
    await DeliveryPage({
      params: Promise.resolve({ id: ID }),
      searchParams: Promise.resolve(resendOutcome ? { resend: resendOutcome } : {}),
    } as never),
  );

beforeEach(() => {
  vi.resetAllMocks();
  mocks.requireApi.mockResolvedValue({ baseUrl: "http://api.test", apiKey: "whk_x" });
});

describe("delivery page", () => {
  it("shows the delivery and each attempt under one level 1 heading", async () => {
    mocks.getDelivery.mockResolvedValue({ ok: true, data: detail() });

    await show();

    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("order.created");
    expect(screen.getByText("https://shop.example.com/hooks")).toBeInTheDocument();
    expect(screen.getByRole("table", { name: "Attempts" })).toBeInTheDocument();
  });

  it("asks the API for the delivery named in the address", async () => {
    mocks.getDelivery.mockResolvedValue({ ok: true, data: detail() });

    await show();

    expect(mocks.getDelivery).toHaveBeenCalledWith({ baseUrl: "http://api.test", apiKey: "whk_x" }, ID);
  });

  it("offers to send a dead delivery again, with a button because it changes something", async () => {
    mocks.getDelivery.mockResolvedValue({ ok: true, data: detail("Dead") });

    await show();

    expect(screen.getByRole("button", { name: "Send again" })).toBeInTheDocument();
  });

  it.each(["Pending", "InProgress", "Succeeded"])("offers nothing to send again while it is %s", async (status) => {
    mocks.getDelivery.mockResolvedValue({ ok: true, data: detail(status) });

    await show();

    expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
  });

  it("links to the event the delivery belongs to", async () => {
    mocks.getDelivery.mockResolvedValue({ ok: true, data: detail() });

    await show();

    expect(screen.getByRole("link", { name: /event/i })).toHaveAttribute(
      "href",
      "/events/c71e04b9-0000-4000-8000-000000000001",
    );
  });

  it.each([
    ["ok", /queued/i, "status"],
    ["failed", /could not be sent again/i, "alert"],
  ])("tells what came of sending it again (%s)", async (outcome, text, role) => {
    mocks.getDelivery.mockResolvedValue({ ok: true, data: detail("Pending") });

    await show(outcome);

    expect(screen.getByRole(role)).toHaveTextContent(text);
  });

  it.each(["Dead", "Succeeded"])("stops saying it was queued once the delivery is %s again", async (status) => {
    // The note comes from the address, which stays the same while the page refreshes by itself.
    mocks.getDelivery.mockResolvedValue({ ok: true, data: detail(status) });

    await show("ok");

    expect(screen.queryByRole("status")).toBeNull();
  });

  it("ignores any other value in the address instead of printing it", async () => {
    mocks.getDelivery.mockResolvedValue({ ok: true, data: detail() });

    const { container } = await show("<b>hi</b>");

    expect(container.querySelector("b")).toBeNull();
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("says the page does not exist when the API does not know the delivery", async () => {
    mocks.getDelivery.mockResolvedValue({ ok: false, kind: "rejected", status: 404 });

    await expect(show()).rejects.toThrow("NOT_FOUND");
  });

  it("makes the visitor sign in again when the key is no longer accepted", async () => {
    mocks.getDelivery.mockResolvedValue({ ok: false, kind: "unauthorized", status: 401 });

    await expect(show()).rejects.toThrow("REDIRECT /session/expired");
  });

  it("shows a message when the API fails", async () => {
    mocks.getDelivery.mockResolvedValue({ ok: false, kind: "server", status: 500 });

    await show();

    expect(screen.getByRole("alert")).toBeInTheDocument();
  });
});
