import { describe, expect, it } from "vitest";
import { MAX_URL_LENGTH, validateEndpointUrl } from "@/lib/endpoints/validate";

describe("validateEndpointUrl", () => {
  it.each(["https://shop.example.com/hooks", "http://localhost:9000/hook", "https://api.example.com:8443/a/b?x=1"])(
    "accepts %s",
    (url) => {
      expect(validateEndpointUrl(url)).toEqual({ ok: true, url });
    },
  );

  it("trims spaces that came along when the address was pasted", () => {
    expect(validateEndpointUrl("  https://shop.example.com/hooks \n")).toEqual({ ok: true, url: "https://shop.example.com/hooks" });
  });

  it.each(["", "   "])("asks for an address when it is %j", (value) => {
    expect(validateEndpointUrl(value)).toEqual({ ok: false, error: "Enter the address of your receiver, for example https://example.com/hooks." });
  });

  it.each(["not a url", "example.com/hooks", "//example.com", "https://"])("says %j is not an address", (value) => {
    expect(validateEndpointUrl(value)).toEqual({ ok: false, error: "That is not a web address. It must start with https:// or http://." });
  });

  it.each(["ftp://example.com", "javascript:alert(1)", "file:///etc/passwd", "ws://example.com"])(
    "refuses %s: only http and https receive events",
    (value) => {
      expect(validateEndpointUrl(value)).toEqual({ ok: false, error: "That is not a web address. It must start with https:// or http://." });
    },
  );

  it.each(["https://user:pw@example.com", "https://user@example.com"])("refuses %s: a name or password in an address ends up in logs", (value) => {
    expect(validateEndpointUrl(value)).toEqual({ ok: false, error: "The address must not contain a user name or password." });
  });

  it("refuses an address that is too long", () => {
    const long = `https://example.com/${"a".repeat(MAX_URL_LENGTH)}`;

    expect(validateEndpointUrl(long)).toEqual({ ok: false, error: `The address can be at most ${MAX_URL_LENGTH} characters.` });
    expect(validateEndpointUrl(`https://example.com/${"a".repeat(MAX_URL_LENGTH - 20)}`).ok).toBe(true);
  });
});
