// @vitest-environment node
import { describe, expect, it } from "vitest";
import nextConfig from "../next.config";

// These settings protect the panel, but nothing else would notice if one of them were changed back.
describe("next.config", () => {
  it("does not turn on the caching models that prepare pages in advance", async () => {
    const config = nextConfig as Record<string, unknown>;

    expect(config.cacheComponents ?? false).toBe(false);
    expect(config.partialPrefetching ?? false).toBe(false);
  });

  it("builds a self-contained server, which is what the container image runs and copies", () => {
    // Without it the image would have to carry every installed package to run `next start`.
    expect(nextConfig.output).toBe("standalone");
  });

  it("does not announce the framework and its version", () => {
    expect(nextConfig.poweredByHeader).toBe(false);
  });

  it("sends the fixed security headers on every route", async () => {
    const rules = await nextConfig.headers?.();
    const everyRoute = rules?.find((rule) => rule.source === "/:path*");
    const value = (key: string) => everyRoute?.headers.find((header) => header.key === key)?.value;

    expect(everyRoute).toBeDefined();
    expect(value("X-Content-Type-Options")).toBe("nosniff");
    expect(value("X-Frame-Options")).toBe("DENY");
    expect(value("Referrer-Policy")).toBe("no-referrer");
    expect(value("Cross-Origin-Opener-Policy")).toBe("same-origin");
    expect(value("Permissions-Policy")).toContain("camera=()");
  });
});
