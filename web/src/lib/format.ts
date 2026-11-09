/** A time as UTC text, the same for everybody: "2026-01-05 09:03:07 UTC". */
export function formatUtc(value: string): string {
  const time = new Date(value);
  if (!value || Number.isNaN(time.getTime())) return "unknown time";

  return `${time.toISOString().slice(0, 19).replace("T", " ")} UTC`;
}

/** How long something took: milliseconds below a second, then seconds. */
export function formatDuration(milliseconds: number): string {
  if (milliseconds < 1000) return `${milliseconds} ms`;
  if (milliseconds < 60_000) return `${(milliseconds / 1000).toFixed(1)} s`;
  return `${Math.round(milliseconds / 1000)} s`;
}
