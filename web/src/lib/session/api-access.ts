import "server-only";
import { redirect } from "next/navigation";
import type { ApiCallOptions } from "@/lib/api/client";
import { readApiBaseUrl } from "@/lib/config";
import { getAccount } from "@/lib/session/current";

/**
 * What a page or a Server Action needs to talk to the API as the signed-in user.
 *
 * Every page and every action calls this itself. A layout does not run again on every navigation, and a
 * Server Action can be called without its page, so neither can be trusted to have checked already.
 * Returns null when the API cannot be reached; the caller shows a message and keeps the session.
 */
export async function requireApi(): Promise<ApiCallOptions | null> {
  const state = await getAccount();

  if (state.status === "signed-out") redirect("/session/expired");
  if (state.status === "api-down") return null;

  return { baseUrl: readApiBaseUrl(), apiKey: state.apiKey };
}
