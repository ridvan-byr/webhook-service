import "server-only";

type Env = Record<string, string | undefined>;

const MIN_SECRET_LENGTH = 32;

/**
 * The secret that the session cookie is encrypted with. There is no default value on purpose: a built-in
 * secret would be known to everyone who reads the source.
 */
export function readSessionSecret(env: Env = process.env): string {
  const secret = env.SESSION_SECRET;

  if (!secret || secret.trim().length < MIN_SECRET_LENGTH) {
    // The message says what is wrong and how to fix it, but never repeats what was given.
    throw new Error(
      `SESSION_SECRET is missing or shorter than ${MIN_SECRET_LENGTH} characters. ` +
        `Make one with: node -e "console.log(require('crypto').randomBytes(32).toString('base64'))" ` +
        `and put it in web/.env.local.`,
    );
  }

  return secret;
}

/** Where the API is. Read on the server only: the browser never talks to the API. */
export function readApiBaseUrl(env: Env = process.env): string {
  const value = env.API_BASE_URL?.trim();
  const invalid = () => new Error("API_BASE_URL must be an http or https address, for example http://localhost:5005.");

  if (!value) throw invalid();

  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw invalid();
  }

  if (!["http:", "https:"].includes(url.protocol) || url.username || url.password) throw invalid();

  return value.replace(/\/+$/, "");
}
