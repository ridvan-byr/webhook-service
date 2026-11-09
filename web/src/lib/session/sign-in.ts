import "server-only";
import { apiGet } from "@/lib/api/client";
import { SESSION_LIFETIME_SECONDS, sessionCookie } from "@/lib/session/cookie";
import { sealSession } from "@/lib/session/crypto";

export type SignInResult = { ok: true } | { ok: false; message: string };

export interface SignInDeps {
  baseUrl: string;
  secret: string;
  now: Date;
  production: boolean;
  fetch?: typeof fetch;
  setCookie: (name: string, value: string, options: ReturnType<typeof sessionCookie>["options"]) => void;
}

const REJECTED = "That API key was not accepted.";
const UNREACHABLE = "The API could not be reached. Try again in a moment.";

/**
 * Checks a pasted API key against the API and, only if the API accepts it, stores it in an encrypted cookie.
 * Every key the API turns down gets the same answer, so the form cannot be used to learn anything about keys.
 */
export async function performSignIn(rawKey: string, deps: SignInDeps): Promise<SignInResult> {
  const apiKey = rawKey.trim();
  if (!apiKey) return { ok: false, message: REJECTED };

  const result = await apiGet("/me", { baseUrl: deps.baseUrl, apiKey, fetch: deps.fetch });

  if (!result.ok) {
    return { ok: false, message: result.kind === "unauthorized" || result.kind === "rejected" ? REJECTED : UNREACHABLE };
  }

  const { name, options } = sessionCookie({ production: deps.production });
  const token = sealSession({ apiKey }, { secret: deps.secret, now: deps.now, lifetimeSeconds: SESSION_LIFETIME_SECONDS });
  deps.setCookie(name, token, options);
  return { ok: true };
}
