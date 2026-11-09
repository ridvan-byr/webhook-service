import { describe, expect, it } from "vitest";
import { SESSION_LIFETIME_SECONDS, expiredSessionCookie, sessionCookie } from "@/lib/session/cookie";

describe("expiredSessionCookie", () => {
  it.each([{ production: true }, { production: false }])(
    "removes the session cookie by overwriting it with the same name and attributes (%o)",
    (environment) => {
      const { name, value, options } = expiredSessionCookie(environment);

      expect(name).toBe(sessionCookie(environment).name);
      expect(value).toBe("");
      expect(options).toEqual({ ...sessionCookie(environment).options, maxAge: 0 });
    },
  );

  it("is still Secure in production: the browser ignores a __Host- cookie that is changed without it", () => {
    // Found by signing out in a real browser: a deletion without Secure was ignored, and the session lived on.
    expect(expiredSessionCookie({ production: true }).options.secure).toBe(true);
  });
});

describe("sessionCookie", () => {
  it("keeps the session for eight hours, no longer", () => {
    expect(SESSION_LIFETIME_SECONDS).toBe(8 * 60 * 60);
    expect(sessionCookie({ production: true }).options.maxAge).toBe(28_800);
  });

  it.each([{ production: true }, { production: false }])(
    "cannot be read by JavaScript and is never sent along with a request from another site (%o)",
    (environment) => {
      const { options } = sessionCookie(environment);

      expect(options.httpOnly).toBe(true);
      expect(options.sameSite).toBe("strict");
      expect(options.path).toBe("/");
    },
  );

  it("is bound to this exact host and to HTTPS in production", () => {
    const { name, options } = sessionCookie({ production: true });

    // The __Host- prefix makes the browser refuse the cookie unless it is Secure, has path "/" and has
    // no Domain attribute, so another subdomain cannot set or overwrite it.
    expect(name).toBe("__Host-session");
    expect(options.secure).toBe(true);
    expect(options).not.toHaveProperty("domain");
  });

  it("works over plain http while developing, where the prefix and Secure would be refused", () => {
    const { name, options } = sessionCookie({ production: false });

    expect(name).toBe("session");
    expect(options.secure).toBe(false);
  });
});
