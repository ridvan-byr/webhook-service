export const MAX_URL_LENGTH = 2048;

export type EndpointUrlResult = { ok: true; url: string } | { ok: false; error: string };

const NOT_A_WEB_ADDRESS = "That is not a web address. It must start with https:// or http://.";

/**
 * A first look at an address, so the form can say what is wrong at once. Whether the address may be used at
 * all (HTTPS only, no private networks) is decided by the API, which is the one place that enforces it.
 */
export function validateEndpointUrl(value: string): EndpointUrlResult {
  const text = value.trim();

  if (!text) return { ok: false, error: "Enter the address of your receiver, for example https://example.com/hooks." };
  if (text.length > MAX_URL_LENGTH) return { ok: false, error: `The address can be at most ${MAX_URL_LENGTH} characters.` };

  let parsed: URL;
  try {
    parsed = new URL(text);
  } catch {
    return { ok: false, error: NOT_A_WEB_ADDRESS };
  }

  if (parsed.protocol !== "https:" && parsed.protocol !== "http:") return { ok: false, error: NOT_A_WEB_ADDRESS };
  if (parsed.username || parsed.password) return { ok: false, error: "The address must not contain a user name or password." };

  return { ok: true, url: text };
}
