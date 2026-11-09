import { describe, expect, it } from "vitest";
import { formatDuration, formatUtc } from "@/lib/format";

describe("formatUtc", () => {
  it("writes a time the same way for every visitor, in UTC, so it means the same on every machine", () => {
    expect(formatUtc("2026-01-05T09:03:07.123+00:00")).toBe("2026-01-05 09:03:07 UTC");
  });

  it("converts a time that carries another offset", () => {
    expect(formatUtc("2026-01-05T23:30:00+03:00")).toBe("2026-01-05 20:30:00 UTC");
  });

  it.each(["", "yesterday", "2026-13-45"])("does not pretend to know what %j means", (value) => {
    expect(formatUtc(value)).toBe("unknown time");
  });
});

describe("formatDuration", () => {
  it.each([
    [0, "0 ms"],
    [87, "87 ms"],
    [999, "999 ms"],
    [1000, "1.0 s"],
    [2450, "2.5 s"],
    [61_000, "61 s"],
  ])("shows %i milliseconds as %s", (ms, text) => {
    expect(formatDuration(ms)).toBe(text);
  });
});
