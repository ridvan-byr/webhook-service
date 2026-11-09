// @vitest-environment node
import { beforeEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  requireApi: vi.fn(),
  resendDelivery: vi.fn(),
  redirect: vi.fn((to: string) => {
    throw new Error(`REDIRECT ${to}`);
  }),
}));

vi.mock("@/lib/session/api-access", () => ({ requireApi: mocks.requireApi }));
vi.mock("@/lib/api/deliveries", () => ({ resendDelivery: mocks.resendDelivery }));
vi.mock("next/navigation", () => ({ redirect: mocks.redirect }));

import { resend } from "@/app/(app)/deliveries/[id]/actions";

const ID = "3f2a91c0-1b2d-4e5f-8a9b-0c1d2e3f4a5b";
const OPTIONS = { baseUrl: "http://api.test", apiKey: "whk_x" };

beforeEach(() => {
  vi.clearAllMocks();
  mocks.requireApi.mockResolvedValue(OPTIONS);
});

describe("resend", () => {
  it("checks who is asking itself, before it does anything", async () => {
    // A Server Action can be called directly, without going through the page that shows its button.
    mocks.requireApi.mockImplementation(async () => {
      throw new Error("REDIRECT /session/expired");
    });

    await expect(resend(ID)).rejects.toThrow("REDIRECT /session/expired");
    expect(mocks.resendDelivery).not.toHaveBeenCalled();
  });

  it("asks the API to send the delivery again and goes back to it", async () => {
    mocks.resendDelivery.mockResolvedValue({ ok: true, data: {} });

    await expect(resend(ID)).rejects.toThrow(`REDIRECT /deliveries/${ID}?resend=ok`);
    expect(mocks.resendDelivery).toHaveBeenCalledWith(OPTIONS, ID);
  });

  it.each([
    [{ ok: false, kind: "rejected", status: 409 }],
    [{ ok: false, kind: "unreachable" }],
    [{ ok: false, kind: "server", status: 500 }],
  ])("goes back and says it did not work (%o)", async (result) => {
    mocks.resendDelivery.mockResolvedValue(result);

    await expect(resend(ID)).rejects.toThrow(`REDIRECT /deliveries/${ID}?resend=failed`);
  });

  it("makes the visitor sign in again when the key is no longer accepted", async () => {
    mocks.resendDelivery.mockResolvedValue({ ok: false, kind: "unauthorized", status: 401 });

    await expect(resend(ID)).rejects.toThrow("REDIRECT /session/expired");
  });

  it("never puts anything but a real id into the address it goes back to", async () => {
    mocks.resendDelivery.mockResolvedValue({ ok: false, kind: "rejected", status: 404 });

    await expect(resend("../../login?x=")).rejects.toThrow("REDIRECT /deliveries?resend=failed");
  });
});
