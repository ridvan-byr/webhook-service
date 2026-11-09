import type { ApiResult } from "@/lib/api/client";

export type Problem = "expired" | "not-found" | "unavailable";

/** What a failed call to the API means for the page that made it. */
export function problemFor(result: Extract<ApiResult<unknown>, { ok: false }>): Problem {
  if (result.kind === "unauthorized") return "expired";
  if (result.kind === "rejected" && result.status === 404) return "not-found";
  return "unavailable";
}
