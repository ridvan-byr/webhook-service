// @vitest-environment node
import { describe, expect, it } from "vitest";
import { SignInLimiter, getSignInLimiter } from "@/lib/session/sign-in-limiter";

function limiter(maxFailures = 3, windowMs = 60_000) {
  let now = 1_000_000;
  const instance = new SignInLimiter({ maxFailures, windowMs, now: () => now });
  return { instance, advance: (ms: number) => (now += ms) };
}

describe("SignInLimiter", () => {
  it("lets sign-ins through until the allowed number of failures is used up", () => {
    const { instance } = limiter(3);

    expect(instance.check()).toEqual({ blocked: false });
    instance.recordFailure();
    instance.recordFailure();
    expect(instance.check()).toEqual({ blocked: false });

    instance.recordFailure();
    expect(instance.check()).toMatchObject({ blocked: true });
  });

  it("says that only the failure that reaches the limit made it block, which is when to write a log line", () => {
    const { instance } = limiter(2);

    expect(instance.recordFailure()).toBe(false);
    expect(instance.recordFailure()).toBe(true);
    expect(instance.recordFailure()).toBe(false);
  });

  it("says how long to wait: until the oldest failure leaves the window, rounded up and never below a second", () => {
    const { instance, advance } = limiter(3, 60_000);
    instance.recordFailure();
    advance(10_000);
    instance.recordFailure();
    advance(10_000);
    instance.recordFailure();

    expect(instance.check()).toEqual({ blocked: true, retryAfterSeconds: 40 });

    advance(39_100);
    expect(instance.check()).toEqual({ blocked: true, retryAfterSeconds: 1 });
  });

  it("rounds the wait up, never to the nearest second: a visitor told 40 seconds must not still be refused at 40", () => {
    const { instance, advance } = limiter(1, 60_000);
    instance.recordFailure();
    advance(19_700);

    // 40.3 seconds are left. "Nearest" would say 40, and trying again then would be refused.
    expect(instance.check()).toEqual({ blocked: true, retryAfterSeconds: 41 });
  });

  it("lets sign-ins through again when the failures are older than the window", () => {
    const { instance, advance } = limiter(2, 60_000);
    instance.recordFailure();
    instance.recordFailure();
    expect(instance.check()).toMatchObject({ blocked: true });

    advance(60_000);

    expect(instance.check()).toEqual({ blocked: false });
  });

  it("slides: failures at the edge of two windows still count together", () => {
    const { instance, advance } = limiter(3, 60_000);
    instance.recordFailure();
    instance.recordFailure();
    advance(59_000);
    instance.recordFailure();

    expect(instance.check()).toMatchObject({ blocked: true });
  });

  it("forgets failures from before the window", () => {
    const { instance, advance } = limiter(3, 60_000);
    instance.recordFailure();
    instance.recordFailure();
    advance(61_000);

    instance.recordFailure();

    expect(instance.check()).toEqual({ blocked: false });
  });

  it("keeps only as many failures as it needs, so it cannot grow without bound", () => {
    const { instance } = limiter(3);

    for (let i = 0; i < 10_000; i++) instance.recordFailure();

    expect(instance.size).toBeLessThanOrEqual(3);
  });

  it.each([
    [0, 60_000],
    [-1, 60_000],
    [1.5, 60_000],
    [3, 0],
    [3, -5],
    [Number.NaN, 60_000],
  ])("refuses settings that would switch the limit off or make no sense (%s failures, %s ms)", (maxFailures, windowMs) => {
    expect(() => new SignInLimiter({ maxFailures, windowMs })).toThrow(/sign-in limit/i);
  });

  it("gives the whole server one limiter, even when the module is loaded again", async () => {
    const first = getSignInLimiter();

    // A second copy of the module, as the server can make for another route.
    const { vi } = await import("vitest");
    vi.resetModules();
    const again = (await import("@/lib/session/sign-in-limiter")).getSignInLimiter();

    expect(again).toBe(first);
  });

  it("allows ten failures a minute by default, fewer than the twenty a minute the API allows", async () => {
    const { DEFAULT_MAX_FAILURES, DEFAULT_WINDOW_MS } = await import("@/lib/session/sign-in-limiter");

    // The panel's sign-in goes through the API, which counts failures from the panel's address too. The panel
    // must stay below the API's own limit, or one visitor could make the API refuse every user of the panel.
    expect(DEFAULT_MAX_FAILURES).toBe(10);
    expect(DEFAULT_WINDOW_MS).toBe(60_000);
    expect(DEFAULT_MAX_FAILURES).toBeLessThan(20);
  });
});
