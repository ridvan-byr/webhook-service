import { describe, expect, it } from "vitest";
import { problemFor } from "@/lib/api/problem";

describe("problemFor", () => {
  it("sends a key the API turned down back to sign in", () => {
    expect(problemFor({ ok: false, kind: "unauthorized", status: 401 })).toBe("expired");
  });

  it("calls a 404 a missing page", () => {
    expect(problemFor({ ok: false, kind: "rejected", status: 404 })).toBe("not-found");
  });

  it.each([
    [{ ok: false, kind: "rejected", status: 400 }],
    [{ ok: false, kind: "rejected", status: 403 }],
    [{ ok: false, kind: "rejected", status: 409 }],
    [{ ok: false, kind: "server", status: 500 }],
    [{ ok: false, kind: "unreachable" }],
  ] as const)("calls everything else the API being unavailable (%o)", (result) => {
    expect(problemFor(result)).toBe("unavailable");
  });
});
