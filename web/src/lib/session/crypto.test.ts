// @vitest-environment node
import { describe, expect, it } from "vitest";
import { openSession, sealSession } from "@/lib/session/crypto";

const SECRET = "0123456789abcdef0123456789abcdef"; // 32 characters
const OTHER_SECRET = "fedcba9876543210fedcba9876543210";
const API_KEY = "whk_0123456789ab_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
const NOW = new Date("2026-11-09T10:00:00Z");

function seal(overrides: { secret?: string; now?: Date; lifetimeSeconds?: number } = {}) {
  return sealSession({ apiKey: API_KEY }, { secret: SECRET, now: NOW, lifetimeSeconds: 8 * 3600, ...overrides });
}

function open(token: string, overrides: { secret?: string; now?: Date } = {}) {
  return openSession(token, { secret: SECRET, now: NOW, ...overrides });
}

/** Changes one character of one part of the token, keeping it well-formed. */
function tamper(token: string, part: 1 | 2 | 3): string {
  const parts = token.split(".");
  const value = parts[part];
  const replacement = value[0] === "A" ? "B" : "A";
  parts[part] = replacement + value.slice(1);
  return parts.join(".");
}

describe("sealSession and openSession", () => {
  it("gives back what was sealed", () => {
    expect(open(seal())).toEqual({ apiKey: API_KEY });
  });

  it("does not open for a different secret", () => {
    expect(open(seal(), { secret: OTHER_SECRET })).toBeNull();
  });

  it("is valid until the lifetime is over and not a second longer", () => {
    const token = seal({ lifetimeSeconds: 60 });

    expect(open(token, { now: new Date(NOW.getTime() + 59_000) })).toEqual({ apiKey: API_KEY });
    expect(open(token, { now: new Date(NOW.getTime() + 60_000) })).toBeNull();
    expect(open(token, { now: new Date(NOW.getTime() + 3_600_000) })).toBeNull();
  });

  it.each([
    ["the initialisation vector", 1 as const],
    ["the encrypted content", 2 as const],
    ["the authentication tag", 3 as const],
  ])("does not open when %s has been changed", (_name, part) => {
    expect(open(tamper(seal(), part))).toBeNull();
  });

  it("does not open under another version label, even with the right content", () => {
    const parts = seal().split(".");
    parts[0] = "v2";

    expect(open(parts.join("."))).toBeNull();
  });

  it("never contains the API key in readable form", () => {
    const token = seal();
    const decoded = [1, 2, 3].map((i) => Buffer.from(token.split(".")[i], "base64url").toString("latin1")).join("");

    expect(token).not.toContain(API_KEY);
    expect(token).not.toContain("whk_");
    expect(decoded).not.toContain("whk_");
  });

  it("gives a different token every time, so two sessions cannot be compared", () => {
    expect(seal()).not.toBe(seal());
  });

  it("has the form version.iv.content.tag, all in the characters that are safe in a cookie", () => {
    expect(seal()).toMatch(/^v1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/);
  });

  it.each([
    ["an empty string", ""],
    ["text that is not a token", "garbage"],
    ["too few parts", "v1.AAAA.BBBB"],
    ["too many parts", "v1.AAAA.BBBB.CCCC.DDDD"],
    ["a vector of the wrong length", "v1.AAAA.BBBB.CCCCCCCCCCCCCCCCCCCCCC"],
    ["empty parts", "v1..."],
    ["characters that are not base64", "v1.!!!!.????.****"],
  ])("does not open %s", (_name, token) => {
    expect(open(token)).toBeNull();
  });

  it("refuses a secret that is too short to be safe, instead of quietly using it", () => {
    expect(() => seal({ secret: "too-short" })).toThrow(/SESSION_SECRET/);
    expect(() => open("v1.a.b.c", { secret: "too-short" })).toThrow(/SESSION_SECRET/);
  });
});
