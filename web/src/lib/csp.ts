const BASE64 = /^[A-Za-z0-9+/]+={0,2}$/;

/** A new unpredictable value for every request: 128 random bits. */
export function createNonce(): string {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return Buffer.from(bytes).toString("base64");
}

export type CspOptions = {
  /** Development needs a few extras for hot reloading. Never true in production. */
  development: boolean;
};

/**
 * The Content-Security-Policy of one response.
 *
 * Scripts and styles may only run when they carry the nonce generated for this very request, so an
 * attacker who manages to inject markup cannot run their own script: they cannot guess the nonce.
 * 'strict-dynamic' lets the scripts that carry the nonce load the rest of the application.
 *
 * There is no 'unsafe-inline' for scripts, and none for styles either. That means the application must
 * never put a style="..." attribute in the HTML it renders: use classes instead.
 */
export function buildContentSecurityPolicy(nonce: string, options: CspOptions): string {
  // The nonce is written into a header. Anything other than base64 characters (a semicolon, a space, a
  // quote, a line break) could change the policy or add a header, so such a value is refused outright.
  if (!BASE64.test(nonce)) {
    throw new Error("The nonce must be a non-empty base64 value.");
  }

  const directives = [
    "default-src 'self'",
    `script-src 'self' 'nonce-${nonce}' 'strict-dynamic'${options.development ? " 'unsafe-eval'" : ""}`,
    `style-src 'self' 'nonce-${nonce}'`,
    "img-src 'self' blob: data:",
    "font-src 'self'",
    // The browser only talks to this application. The API is called by the server, never by the browser.
    `connect-src 'self'${options.development ? " ws:" : ""}`,
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
    "frame-ancestors 'none'",
  ];

  return directives.join("; ");
}
