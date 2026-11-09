import "server-only";
import { apiGet } from "@/lib/api/client";
import { openSession } from "@/lib/session/crypto";

interface MeResponse {
  tenantName: string;
  apiKeyName: string;
}

export type AccountState =
  | { status: "ok"; apiKey: string; account: { tenantName: string; keyName: string } }
  | { status: "signed-out" }
  | { status: "api-down" };

export interface AccountDeps {
  secret: string;
  now: Date;
  baseUrl: string;
  fetch?: typeof fetch;
}

/**
 * Works out who is looking at the panel from the session cookie, and asks the API whether the key inside
 * it still works. Doing that on every page is what makes a revoked key stop working at once.
 */
export async function loadAccount(cookieValue: string | undefined, deps: AccountDeps): Promise<AccountState> {
  const session = cookieValue ? openSession(cookieValue, { secret: deps.secret, now: deps.now }) : null;
  if (!session) return { status: "signed-out" };

  const me = await apiGet<MeResponse>("/me", { baseUrl: deps.baseUrl, apiKey: session.apiKey, fetch: deps.fetch });

  if (me.ok) {
    return {
      status: "ok",
      apiKey: session.apiKey,
      account: { tenantName: me.data.tenantName, keyName: me.data.apiKeyName },
    };
  }

  // Only a clear "no" from the API ends the session. An outage must not sign everybody out.
  return me.kind === "unauthorized" ? { status: "signed-out" } : { status: "api-down" };
}
