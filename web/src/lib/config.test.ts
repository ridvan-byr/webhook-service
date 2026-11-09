import { describe, expect, it } from "vitest";
import { readApiBaseUrl, readSessionSecret } from "@/lib/config";

describe("readSessionSecret", () => {
  const SECRET = "0123456789abcdef0123456789abcdef";

  it("returns a secret that is long enough", () => {
    expect(readSessionSecret({ SESSION_SECRET: SECRET })).toBe(SECRET);
  });

  it.each([
    ["missing", {}],
    ["empty", { SESSION_SECRET: "" }],
    ["only spaces", { SESSION_SECRET: "                                   " }],
    ["too short", { SESSION_SECRET: "short-secret" }],
    ["one character too short", { SESSION_SECRET: SECRET.slice(1) }],
  ])("refuses a secret that is %s", (_why, env) => {
    expect(() => readSessionSecret(env)).toThrow(/SESSION_SECRET/);
  });

  it("tells how to make a good one, and never repeats the value it was given", () => {
    const attempt = "short-secret-that-must-not-appear";

    expect(() => readSessionSecret({ SESSION_SECRET: attempt.slice(0, 20) })).toThrow(/randomBytes|openssl/i);
    try {
      readSessionSecret({ SESSION_SECRET: attempt.slice(0, 20) });
    } catch (error) {
      expect(String(error)).not.toContain(attempt.slice(0, 20));
    }
  });
});

describe("readApiBaseUrl", () => {
  it.each([
    ["http://localhost:5005", "http://localhost:5005"],
    ["http://localhost:5005/", "http://localhost:5005"],
    ["https://api.example.com/v1/", "https://api.example.com/v1"],
    ["  https://api.example.com  ", "https://api.example.com"],
  ])("accepts %s and uses %s", (value, expected) => {
    expect(readApiBaseUrl({ API_BASE_URL: value })).toBe(expected);
  });

  it.each([
    ["missing", {}],
    ["empty", { API_BASE_URL: "" }],
    ["not an address", { API_BASE_URL: "not a url" }],
    ["not http or https", { API_BASE_URL: "ftp://example.com" }],
    ["without a scheme", { API_BASE_URL: "localhost:5005" }],
    ["carrying a user name and password", { API_BASE_URL: "https://user:pw@example.com" }],
  ])("refuses an address that is %s", (_why, env) => {
    expect(() => readApiBaseUrl(env)).toThrow(/API_BASE_URL/);
  });
});
