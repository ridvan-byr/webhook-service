import "server-only";
import { apiGet } from "@/lib/api/client";
import { SESSION_LIFETIME_SECONDS, sessionCookie } from "@/lib/session/cookie";
import { sealSession } from "@/lib/session/crypto";
import type { SignInLimiter } from "@/lib/session/sign-in-limiter";

export type SignInResult = { ok: true } | { ok: false; message: string };

export interface SignInDeps {
  baseUrl: string;
  secret: string;
  now: Date;
  production: boolean;
  fetch?: typeof fetch;
  setCookie: (name: string, value: string, options: ReturnType<typeof sessionCookie>["options"]) => void;
  /** Counts failed sign-ins. After too many, the API is not asked at all for a while. */
  limiter: SignInLimiter;
  /** Called once, when the limit is reached, so it can be written to the log. */
  onLimitReached?: () => void;
}

const REJECTED = "That API key was not accepted.";
const UNREACHABLE = "The API could not be reached. Try again in a moment.";
const API_REFUSED = "The API is not accepting sign-ins right now. Try again in a minute.";

const seconds = (n: number) => (n === 1 ? "1 second" : `${n} seconds`);

/**
 * Checks a pasted API key against the API and, only if the API accepts it, stores it in an encrypted cookie.
 * Every key the API turns down gets the same answer, so the form cannot be used to learn anything about keys.
 * Too many keys turned down in a short time, and the API is not asked until a while has passed.
 */
export async function performSignIn(rawKey: string, deps: SignInDeps): Promise<SignInResult> {
  const apiKey = rawKey.trim();
  if (!apiKey) return { ok: false, message: REJECTED };

  const gate = deps.limiter.check();
  if (gate.blocked) {
    return { ok: false, message: `Too many failed attempts. Try again in ${seconds(gate.retryAfterSeconds)}.` };
  }

  const result = await apiGet("/me", { baseUrl: deps.baseUrl, apiKey, fetch: deps.fetch });

  if (!result.ok) {
    // A 429 means the API itself refused for a while. That is not a key the API turned down, so it is not
    // counted and not described as one.
    if (result.kind === "rejected" && result.status === 429) return { ok: false, message: API_REFUSED };

    if (result.kind === "unauthorized" || result.kind === "rejected") {
      if (deps.limiter.recordFailure()) deps.onLimitReached?.();
      return { ok: false, message: REJECTED };
    }

    return { ok: false, message: UNREACHABLE };
  }

  const { name, options } = sessionCookie({ production: deps.production });
  const token = sealSession({ apiKey }, { secret: deps.secret, now: deps.now, lifetimeSeconds: SESSION_LIFETIME_SECONDS });
  deps.setCookie(name, token, options);
  return { ok: true };
}
