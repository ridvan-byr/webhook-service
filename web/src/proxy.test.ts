// @vitest-environment node
import { NextRequest } from "next/server";
import { describe, expect, it } from "vitest";
import { proxy } from "@/proxy";

function request(path = "/deliveries", cookie: string | null = "session=anything") {
  return new NextRequest(`http://localhost:3000${path}`, cookie ? { headers: { cookie } } : undefined);
}

function nonceOf(policy: string): string {
  const match = /'nonce-([A-Za-z0-9+/=]+)'/.exec(policy);
  if (!match) throw new Error(`No nonce in the policy: ${policy}`);
  return match[1];
}

describe("proxy", () => {
  it("puts a Content-Security-Policy with a nonce on the response", () => {
    const response = proxy(request());

    const policy = response.headers.get("Content-Security-Policy");
    expect(policy).not.toBeNull();
    expect(policy).toContain("script-src 'self' 'nonce-");
  });

  it("gives every request a nonce of its own", () => {
    const first = nonceOf(proxy(request()).headers.get("Content-Security-Policy")!);
    const second = nonceOf(proxy(request()).headers.get("Content-Security-Policy")!);

    expect(first).not.toBe(second);
  });

  it("hands the same nonce to the page that is rendered, so its scripts can carry it", () => {
    const response = proxy(request());

    const inPolicy = nonceOf(response.headers.get("Content-Security-Policy")!);
    // Next.js passes headers set on the request to the render under this name.
    expect(response.headers.get("x-middleware-request-x-nonce")).toBe(inPolicy);
    expect(response.headers.get("x-middleware-request-content-security-policy")).toContain(`'nonce-${inPolicy}'`);
  });

  it("does not let a nonce sent by the client through", () => {
    const hostile = new NextRequest("http://localhost:3000/deliveries", {
      headers: { "x-nonce": "AAAAAAAAAAAAAAAAAAAAAA==" },
    });

    const response = proxy(hostile);

    expect(response.headers.get("x-middleware-request-x-nonce")).not.toBe("AAAAAAAAAAAAAAAAAAAAAA==");
  });
});

describe("proxy: visitors without a session", () => {
  it.each(["/", "/deliveries", "/endpoints", "/events/new"])("sends a visitor of %s to the sign-in page", (path) => {
    const response = proxy(request(path, null));

    expect(response.status).toBe(307);
    expect(new URL(response.headers.get("location")!).pathname).toBe("/login");
  });

  it.each(["/login", "/session/expired"])("lets a visitor reach %s", (path) => {
    const response = proxy(request(path, null));

    expect(response.headers.get("location")).toBeNull();
  });

  it("does not take a similar-looking path for the sign-in page", () => {
    for (const path of ["/loginx", "/login/more", "/session/expired/more"]) {
      expect(proxy(request(path, null)).headers.get("location"), path).not.toBeNull();
    }
  });

  it("lets someone with a session cookie through; whether it is valid is for the page and the API to decide", () => {
    const response = proxy(request("/deliveries", "session=whatever"));

    expect(response.headers.get("location")).toBeNull();
  });

  it("still sends the Content-Security-Policy when it redirects", () => {
    const response = proxy(request("/deliveries", null));

    expect(response.headers.get("Content-Security-Policy")).toContain("script-src 'self' 'nonce-");
  });

  it("does not treat a cookie of another name as a session", () => {
    const response = proxy(request("/deliveries", "other=1; sessionx=2"));

    expect(response.headers.get("location")).not.toBeNull();
  });
});
