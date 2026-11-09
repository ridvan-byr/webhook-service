import "server-only";
import { createCipheriv, createDecipheriv, hkdfSync, randomBytes } from "node:crypto";

/**
 * Turns the session (the API key of the signed-in user and when it ends) into a token that is safe to
 * keep in a cookie, and back.
 *
 * The token is encrypted with AES-256-GCM, so nobody who holds it can read the key, and authenticated, so
 * nobody can change it (including the time it ends) without it being noticed. The time it ends is inside
 * the encrypted part: it cannot be moved forward by editing the cookie.
 *
 * Form: v1.<initialisation vector>.<encrypted content>.<authentication tag>, each part in base64url.
 */

const VERSION = "v1";
const MIN_SECRET_LENGTH = 32;
const IV_BYTES = 12;
const TAG_BYTES = 16;
const BASE64URL = /^[A-Za-z0-9_-]+$/;

export type SessionPayload = { apiKey: string };

type SealOptions = { secret: string; now: Date; lifetimeSeconds: number };
type OpenOptions = { secret: string; now: Date };

export function sealSession(payload: SessionPayload, options: SealOptions): string {
  const key = deriveKey(options.secret);
  const expiresAt = Math.floor(options.now.getTime() / 1000) + options.lifetimeSeconds;
  const iv = randomBytes(IV_BYTES);

  const cipher = createCipheriv("aes-256-gcm", key, iv, { authTagLength: TAG_BYTES });
  // The version is authenticated too: a token cannot be passed off as another version of the format.
  cipher.setAAD(Buffer.from(VERSION));
  const content = Buffer.concat([
    cipher.update(JSON.stringify({ k: payload.apiKey, e: expiresAt }), "utf8"),
    cipher.final(),
  ]);

  return [VERSION, iv, content, cipher.getAuthTag()]
    .map((part) => (typeof part === "string" ? part : part.toString("base64url")))
    .join(".");
}

/**
 * Returns the session, or null for any problem at all: wrong format, wrong secret, changed content, over.
 * The reason is deliberately not reported; it would only help someone who is trying things out.
 */
export function openSession(token: string, options: OpenOptions): SessionPayload | null {
  const key = deriveKey(options.secret);

  const parts = token.split(".");
  if (parts.length !== 4 || parts[0] !== VERSION) return null;
  if (!parts.slice(1).every((part) => BASE64URL.test(part))) return null;

  const iv = Buffer.from(parts[1], "base64url");
  const content = Buffer.from(parts[2], "base64url");
  const tag = Buffer.from(parts[3], "base64url");
  if (iv.length !== IV_BYTES || tag.length !== TAG_BYTES || content.length === 0) return null;

  let plain: string;
  try {
    const decipher = createDecipheriv("aes-256-gcm", key, iv, { authTagLength: TAG_BYTES });
    decipher.setAAD(Buffer.from(VERSION));
    decipher.setAuthTag(tag);
    plain = Buffer.concat([decipher.update(content), decipher.final()]).toString("utf8");
  } catch {
    return null;
  }

  const session = parse(plain);
  if (!session) return null;

  const nowSeconds = Math.floor(options.now.getTime() / 1000);
  return nowSeconds < session.expiresAt ? { apiKey: session.apiKey } : null;
}

function parse(plain: string): { apiKey: string; expiresAt: number } | null {
  try {
    const value: unknown = JSON.parse(plain);
    if (typeof value !== "object" || value === null) return null;

    const { k, e } = value as Record<string, unknown>;
    return typeof k === "string" && k.length > 0 && typeof e === "number" && Number.isFinite(e)
      ? { apiKey: k, expiresAt: e }
      : null;
  } catch {
    return null;
  }
}

/** One key per purpose, derived from the secret, so the secret itself is never used to encrypt. */
function deriveKey(secret: string): Buffer {
  if (Buffer.byteLength(secret) < MIN_SECRET_LENGTH) {
    throw new Error(`SESSION_SECRET must be at least ${MIN_SECRET_LENGTH} characters long.`);
  }

  return Buffer.from(hkdfSync("sha256", secret, "webhook-panel", "session cookie v1", 32));
}
