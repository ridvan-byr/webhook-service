import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Endpoint } from "@/lib/api/endpoints";

const mocks = vi.hoisted(() => ({ requireApi: vi.fn(), listEndpoints: vi.fn() }));

vi.mock("@/lib/session/api-access", () => ({ requireApi: mocks.requireApi }));
vi.mock("@/lib/api/endpoints", () => ({ listEndpoints: mocks.listEndpoints }));
vi.mock("@/app/(app)/endpoints/actions", () => ({ addEndpoint: vi.fn(), setActive: vi.fn() }));
vi.mock("next/navigation", () => ({
  redirect: (to: string) => {
    throw new Error(`REDIRECT ${to}`);
  },
}));

import EndpointsPage from "@/app/(app)/endpoints/page";

const endpoint = (): Endpoint => ({
  id: "e1e1e1e1-0000-4000-8000-000000000002",
  url: "https://shop.example.com/hooks",
  isActive: true,
  createdAt: "2026-01-05T08:00:00+00:00",
});

const show = async (toggle?: string) =>
  render(await EndpointsPage({ searchParams: Promise.resolve(toggle ? { toggle } : {}) } as never));

beforeEach(() => {
  vi.resetAllMocks();
  mocks.requireApi.mockResolvedValue({ baseUrl: "http://api.test", apiKey: "whk_x" });
});

describe("endpoints page", () => {
  it("lists the endpoints and offers to add one, under one level 1 heading", async () => {
    mocks.listEndpoints.mockResolvedValue({ ok: true, data: [endpoint()] });

    await show();

    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Endpoints");
    expect(screen.getByRole("table", { name: "Endpoints" })).toBeInTheDocument();
    expect(screen.getByLabelText("Endpoint address")).toBeInTheDocument();
  });

  it("asks the API with the key of the signed-in user", async () => {
    mocks.listEndpoints.mockResolvedValue({ ok: true, data: [] });

    await show();

    expect(mocks.listEndpoints).toHaveBeenCalledWith({ baseUrl: "http://api.test", apiKey: "whk_x" });
  });

  it("invites the visitor to add the first endpoint, and keeps the form", async () => {
    mocks.listEndpoints.mockResolvedValue({ ok: true, data: [] });

    await show();

    expect(screen.getByText(/no endpoints yet/i)).toBeInTheDocument();
    expect(screen.queryByRole("table")).toBeNull();
    expect(screen.getByLabelText("Endpoint address")).toBeInTheDocument();
  });

  it("says when switching an endpoint did not work", async () => {
    mocks.listEndpoints.mockResolvedValue({ ok: true, data: [endpoint()] });

    await show("failed");

    expect(screen.getByRole("alert")).toHaveTextContent(/could not be changed/i);
  });

  it.each(["ok", "<b>x</b>", "constructor"])("ignores %j in the address instead of printing it", async (toggle) => {
    mocks.listEndpoints.mockResolvedValue({ ok: true, data: [endpoint()] });

    const { container } = await show(toggle);

    expect(screen.queryByRole("alert")).toBeNull();
    expect(container.querySelector("b")).toBeNull();
  });

  it("makes the visitor sign in again when the key is no longer accepted", async () => {
    mocks.listEndpoints.mockResolvedValue({ ok: false, kind: "unauthorized", status: 401 });

    await expect(show()).rejects.toThrow("REDIRECT /session/expired");
  });

  it("shows a message when the API fails, and no form that could not work", async () => {
    mocks.listEndpoints.mockResolvedValue({ ok: false, kind: "server", status: 500 });

    await show();

    expect(screen.getByRole("alert")).toBeInTheDocument();
    expect(screen.queryByLabelText("Endpoint address")).toBeNull();
  });

  it("shows the same message without asking when the API is down beforehand", async () => {
    mocks.requireApi.mockResolvedValue(null);

    await show();

    expect(screen.getByRole("alert")).toBeInTheDocument();
    expect(mocks.listEndpoints).not.toHaveBeenCalled();
  });
});
