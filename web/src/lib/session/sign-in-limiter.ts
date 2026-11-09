/**
 * Allowed failures per window. The panel signs people in through the API, and the API counts failed attempts
 * from the panel's address too (and refuses at 20 a minute). The panel has to stay below that, or one visitor
 * could make the API refuse every user of the panel. So it is stricter than the API.
 */
export const DEFAULT_MAX_FAILURES = 10;
export const DEFAULT_WINDOW_MS = 60_000;

export type SignInCheck = { blocked: false } | { blocked: true; retryAfterSeconds: number };

export interface SignInLimiterOptions {
  maxFailures?: number;
  windowMs?: number;
  /** The clock, in milliseconds. Tests pass their own. */
  now?: () => number;
}

/**
 * Counts failed sign-ins and says when there have been too many.
 *
 * There is one count for the whole panel, not one per visitor: the panel cannot tell visitors apart safely (the
 * address in a request header can be made up by the visitor unless a trusted proxy sets it). The cost is that
 * someone can use up the allowance and make sign-in wait for up to a minute. Signed-in sessions are not
 * affected, only the sign-in form. The count lives in memory, so it starts again with the process.
 */
export class SignInLimiter {
  private readonly maxFailures: number;
  private readonly windowMs: number;
  private readonly now: () => number;
  private failures: number[] = [];

  constructor({ maxFailures = DEFAULT_MAX_FAILURES, windowMs = DEFAULT_WINDOW_MS, now = Date.now }: SignInLimiterOptions = {}) {
    // The limit is a protection, so settings that would switch it off by accident stop the server.
    if (!Number.isInteger(maxFailures) || maxFailures < 1) {
      throw new Error("The sign-in limit must allow a whole number of failures, at least 1.");
    }
    if (!Number.isFinite(windowMs) || windowMs <= 0) {
      throw new Error("The sign-in limit must count failures over a time longer than zero.");
    }

    this.maxFailures = maxFailures;
    this.windowMs = windowMs;
    this.now = now;
  }

  /** How many failures are remembered. Never more than the number allowed. */
  get size(): number {
    return this.failures.length;
  }

  check(): SignInCheck {
    const now = this.now();
    this.dropExpired(now);

    if (this.failures.length < this.maxFailures) return { blocked: false };

    // Still blocked means the oldest failure is younger than the window, so there is a wait left; rounding it up
    // makes it at least one second, so a visitor is never told to try again "in 0 seconds".
    const waitMs = this.failures[0] + this.windowMs - now;
    return { blocked: true, retryAfterSeconds: Math.ceil(waitMs / 1000) };
  }

  /** Counts a failure. Returns true for the one that makes sign-in blocked, which is when to write a log line. */
  recordFailure(): boolean {
    const now = this.now();
    this.dropExpired(now);

    const before = this.failures.length;
    this.failures.push(now);

    // Only the newest failures matter, so the list never grows past the limit.
    while (this.failures.length > this.maxFailures) this.failures.shift();

    return before < this.maxFailures && this.failures.length >= this.maxFailures;
  }

  private dropExpired(now: number) {
    while (this.failures.length > 0 && now - this.failures[0] >= this.windowMs) this.failures.shift();
  }
}

const KEY = Symbol.for("webhook-service.sign-in-limiter");

/**
 * The one limiter of this server. It is kept on globalThis because the server can load a module more than
 * once (for example once per route), and each copy would otherwise count on its own.
 */
export function getSignInLimiter(): SignInLimiter {
  const holder = globalThis as unknown as Record<symbol, SignInLimiter | undefined>;
  return (holder[KEY] ??= new SignInLimiter());
}
