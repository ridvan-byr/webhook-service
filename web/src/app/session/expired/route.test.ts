// @vitest-environment node
import { NextRequest } from "next/server";
import { describe, expect, it } from "vitest";
import { GET } from "@/app/session/expired/route";

describe("GET /session/expired", () => {
  const call = () => GET(new NextRequest("http://localhost:3000/session/expired", { headers: { cookie: "session=stale" } }));

  it("sends the visitor to the sign-in page and says the session ended", async () => {
    const response = await call();

    expect(response.status).toBe(307);
    const location = new URL(response.headers.get("location")!);
    expect(location.pathname).toBe("/login");
    expect(location.searchParams.get("reason")).toBe("expired");
  });

  it("throws the stale cookie away, so the visitor does not bounce back and forth", async () => {
    const response = await call();

    const cookie = response.headers.get("set-cookie")!;
    expect(cookie).toMatch(/^session=;/);
    expect(cookie).toMatch(/Max-Age=0|Expires=Thu, 01 Jan 1970/i);
    expect(cookie).toMatch(/Path=\//i);
  });
});
