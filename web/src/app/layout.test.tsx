import { describe, expect, it } from "vitest";
import { dynamic } from "@/app/layout";

describe("root layout", () => {
  it("makes every page render again for each request", () => {
    // The Content-Security-Policy nonce is new for every request, and what a page shows belongs to one
    // signed-in tenant. A page prepared in advance would break the first and could leak the second.
    expect(dynamic).toBe("force-dynamic");
  });
});
