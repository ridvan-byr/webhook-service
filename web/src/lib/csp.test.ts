import { describe, expect, it } from "vitest";
import { buildContentSecurityPolicy, createNonce } from "@/lib/csp";

const NONCE = "dGVzdC1ub25jZS0xMjM0NQ==";

function directive(policy: string, name: string): string {
  const found = policy.split("; ").find((part) => part.startsWith(`${name} `));
  if (!found) throw new Error(`The policy has no ${name} directive: ${policy}`);
  return found;
}

describe("buildContentSecurityPolicy", () => {
  it("lets scripts and styles run only when they carry the nonce of this request", () => {
    const policy = buildContentSecurityPolicy(NONCE, { development: false });

    expect(policy).toContain(`script-src 'self' 'nonce-${NONCE}' 'strict-dynamic'`);
    expect(policy).toContain(`style-src 'self' 'nonce-${NONCE}'`);
  });

  it.each([{ development: false }, { development: true }])(
    "never allows inline scripts or styles (%o)",
    (options) => {
      const policy = buildContentSecurityPolicy(NONCE, options);

      expect(policy).not.toContain("unsafe-inline");
    },
  );

  it("allows eval and websockets only in development", () => {
    const production = buildContentSecurityPolicy(NONCE, { development: false });
    const development = buildContentSecurityPolicy(NONCE, { development: true });

    expect(production).not.toContain("unsafe-eval");
    expect(directive(production, "connect-src")).toBe("connect-src 'self'");

    expect(directive(development, "script-src")).toContain("'unsafe-eval'");
    expect(directive(development, "connect-src")).toContain("ws:");
  });

  it("forbids plugins, framing, foreign base addresses and foreign form targets", () => {
    const policy = buildContentSecurityPolicy(NONCE, { development: false });

    expect(directive(policy, "default-src")).toBe("default-src 'self'");
    expect(directive(policy, "object-src")).toBe("object-src 'none'");
    expect(directive(policy, "base-uri")).toBe("base-uri 'self'");
    expect(directive(policy, "form-action")).toBe("form-action 'self'");
    expect(directive(policy, "frame-ancestors")).toBe("frame-ancestors 'none'");
  });

  it.each([
    ["a semicolon, which starts a new directive", "abc; script-src *"],
    ["a space, which starts a new source", "abc https://evil.example"],
    ["a quote, which closes the nonce", "abc'"],
    ["a line break", "abc\r\nSet-Cookie: x=1"],
    ["an empty value", ""],
  ])("refuses a nonce with %s", (_reason, nonce) => {
    expect(() => buildContentSecurityPolicy(nonce, { development: false })).toThrow(/nonce/i);
  });
});

describe("createNonce", () => {
  it("is 128 random bits written in base64", () => {
    const nonce = createNonce();

    expect(nonce).toMatch(/^[A-Za-z0-9+/]{22}==$/);
  });

  it("is different every time", () => {
    const nonces = new Set(Array.from({ length: 2000 }, () => createNonce()));

    expect(nonces.size).toBe(2000);
  });

  it("can be used in a policy", () => {
    expect(() => buildContentSecurityPolicy(createNonce(), { development: false })).not.toThrow();
  });
});
