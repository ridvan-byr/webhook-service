// @vitest-environment node
import { beforeEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  getAccount: vi.fn(),
  redirect: vi.fn((to: string) => {
    throw new Error(`REDIRECT ${to}`);
  }),
}));

vi.mock("@/lib/session/current", () => ({ getAccount: mocks.getAccount }));
vi.mock("next/navigation", () => ({ redirect: mocks.redirect }));

import { requireApi } from "@/lib/session/api-access";

beforeEach(() => {
  vi.clearAllMocks();
  vi.stubEnv("API_BASE_URL", "http://api.test:5005/");
});

describe("requireApi", () => {
  it("gives what is needed to call the API as the signed-in user", async () => {
    mocks.getAccount.mockResolvedValue({ status: "ok", apiKey: "whk_x", account: { tenantName: "A", keyName: "k" } });

    expect(await requireApi()).toEqual({ baseUrl: "http://api.test:5005", apiKey: "whk_x" });
  });

  it("sends a visitor without a good session to sign in again", async () => {
    mocks.getAccount.mockResolvedValue({ status: "signed-out" });

    await expect(requireApi()).rejects.toThrow("REDIRECT /session/expired");
  });

  it("gives nothing, and keeps the session, when the API is down", async () => {
    mocks.getAccount.mockResolvedValue({ status: "api-down" });

    expect(await requireApi()).toBeNull();
    expect(mocks.redirect).not.toHaveBeenCalled();
  });
});
