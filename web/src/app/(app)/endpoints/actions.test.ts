// @vitest-environment node
import { beforeEach, describe, expect, it, vi } from "vitest";
import { INITIAL_STATE } from "@/lib/endpoints/state";

const mocks = vi.hoisted(() => ({
  requireApi: vi.fn(),
  createEndpoint: vi.fn(),
  setEndpointActive: vi.fn(),
  revalidatePath: vi.fn(),
  redirect: vi.fn((to: string) => {
    throw new Error(`REDIRECT ${to}`);
  }),
}));

vi.mock("@/lib/session/api-access", () => ({ requireApi: mocks.requireApi }));
vi.mock("@/lib/api/endpoints", () => ({
  createEndpoint: mocks.createEndpoint,
  setEndpointActive: mocks.setEndpointActive,
}));
vi.mock("next/navigation", () => ({ redirect: mocks.redirect }));
vi.mock("next/cache", () => ({ revalidatePath: mocks.revalidatePath }));

import { addEndpoint, setActive } from "@/app/(app)/endpoints/actions";

const OPTIONS = { baseUrl: "http://api.test", apiKey: "whk_x" };
const ID = "e1e1e1e1-0000-4000-8000-000000000002";
const form = (url: string) => {
  const data = new FormData();
  data.set("url", url);
  return data;
};

beforeEach(() => {
  vi.clearAllMocks();
  mocks.requireApi.mockResolvedValue(OPTIONS);
});

describe("addEndpoint", () => {
  it("checks who is asking itself, before it reads anything", async () => {
    mocks.requireApi.mockImplementation(async () => {
      throw new Error("REDIRECT /session/expired");
    });

    await expect(addEndpoint(INITIAL_STATE, form("https://a.example"))).rejects.toThrow("REDIRECT /session/expired");
    expect(mocks.createEndpoint).not.toHaveBeenCalled();
  });

  it("registers the address and hands back the secret, once, in this response only", async () => {
    mocks.createEndpoint.mockResolvedValue({
      ok: true,
      data: { id: ID, url: "https://shop.example.com/hooks", isActive: true, createdAt: "x", signingSecret: "whsec_abc" },
    });

    const state = await addEndpoint(INITIAL_STATE, form("  https://shop.example.com/hooks "));

    expect(mocks.createEndpoint).toHaveBeenCalledWith(OPTIONS, "https://shop.example.com/hooks");
    expect(state).toEqual({
      error: null,
      message: null,
      value: "",
      created: { url: "https://shop.example.com/hooks", secret: "whsec_abc" },
    });
    expect(mocks.revalidatePath).toHaveBeenCalledWith("/endpoints");
  });

  it("says what is wrong with the address without asking the API", async () => {
    const state = await addEndpoint(INITIAL_STATE, form("ftp://x"));

    expect(state.error).toMatch(/web address/i);
    expect(state.value).toBe("ftp://x");
    expect(mocks.createEndpoint).not.toHaveBeenCalled();
  });

  it("explains in its own words that the API refused the address, and keeps what was typed", async () => {
    mocks.createEndpoint.mockResolvedValue({ ok: false, kind: "rejected", status: 400 });

    const state = await addEndpoint(INITIAL_STATE, form("http://10.0.0.5/hook"));

    expect(state.error).toMatch(/https/i);
    expect(state.error).toMatch(/private/i);
    expect(state.value).toBe("http://10.0.0.5/hook");
    expect(state.created).toBeNull();
  });

  it.each([
    [{ ok: false, kind: "unreachable" }],
    [{ ok: false, kind: "server", status: 500 }],
    [{ ok: false, kind: "rejected", status: 409 }],
  ])("says it did not work and keeps what was typed (%o)", async (result) => {
    mocks.createEndpoint.mockResolvedValue(result);

    const state = await addEndpoint(INITIAL_STATE, form("https://a.example"));

    expect(state.message).toBe("The endpoint could not be added. Try again in a moment.");
    expect(state.value).toBe("https://a.example");
    expect(state.created).toBeNull();
  });

  it("never carries a secret in a failure", async () => {
    mocks.createEndpoint.mockResolvedValue({ ok: false, kind: "server", status: 500 });

    expect(JSON.stringify(await addEndpoint(INITIAL_STATE, form("https://a.example")))).not.toMatch(/whsec_|secret"\s*:\s*"/);
  });

  it("makes the visitor sign in again when the key is no longer accepted", async () => {
    mocks.createEndpoint.mockResolvedValue({ ok: false, kind: "unauthorized", status: 401 });

    await expect(addEndpoint(INITIAL_STATE, form("https://a.example"))).rejects.toThrow("REDIRECT /session/expired");
  });

  it("keeps the session and says so when the API is down", async () => {
    mocks.requireApi.mockResolvedValue(null);

    const state = await addEndpoint(INITIAL_STATE, form("https://a.example"));

    expect(state.message).toMatch(/could not be reached/i);
    expect(mocks.createEndpoint).not.toHaveBeenCalled();
  });

  it("treats a missing field as empty instead of failing", async () => {
    const state = await addEndpoint(INITIAL_STATE, new FormData());

    expect(state.error).toMatch(/enter the address/i);
  });
});

describe("setActive", () => {
  it("checks who is asking itself, before it does anything", async () => {
    mocks.requireApi.mockImplementation(async () => {
      throw new Error("REDIRECT /session/expired");
    });

    await expect(setActive(ID, false)).rejects.toThrow("REDIRECT /session/expired");
    expect(mocks.setEndpointActive).not.toHaveBeenCalled();
  });

  it.each([true, false])("switches the endpoint to %s and shows the list again", async (isActive) => {
    mocks.setEndpointActive.mockResolvedValue({ ok: true, data: {} });

    await expect(setActive(ID, isActive)).rejects.toThrow("REDIRECT /endpoints");
    expect(mocks.setEndpointActive).toHaveBeenCalledWith(OPTIONS, ID, isActive);
    expect(mocks.revalidatePath).toHaveBeenCalledWith("/endpoints");
  });

  it.each([
    [{ ok: false, kind: "rejected", status: 404 }],
    [{ ok: false, kind: "unreachable" }],
    [{ ok: false, kind: "server", status: 500 }],
  ])("goes back and says it did not work (%o)", async (result) => {
    mocks.setEndpointActive.mockResolvedValue(result);

    await expect(setActive(ID, true)).rejects.toThrow("REDIRECT /endpoints?toggle=failed");
  });

  it("does not pass on anything but a real true or false", async () => {
    mocks.setEndpointActive.mockResolvedValue({ ok: false, kind: "rejected", status: 400 });

    await expect(setActive(ID, "yes" as unknown as boolean)).rejects.toThrow("REDIRECT /endpoints?toggle=failed");
    expect(mocks.setEndpointActive).toHaveBeenCalledWith(OPTIONS, ID, false);
  });

  it("makes the visitor sign in again when the key is no longer accepted", async () => {
    mocks.setEndpointActive.mockResolvedValue({ ok: false, kind: "unauthorized", status: 401 });

    await expect(setActive(ID, true)).rejects.toThrow("REDIRECT /session/expired");
  });

  it("keeps the session and goes back with a failure note when the API is down", async () => {
    mocks.requireApi.mockResolvedValue(null);

    await expect(setActive(ID, true)).rejects.toThrow("REDIRECT /endpoints?toggle=failed");
  });
});
