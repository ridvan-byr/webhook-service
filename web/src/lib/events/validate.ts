// The limits are the API's own. They are checked here as well so the form can say what is wrong
// next to the field, instead of showing a general failure after a round trip.
export const MAX_TYPE_LENGTH = 200;
export const MAX_KEY_LENGTH = 200;
export const MAX_PAYLOAD_LENGTH = 256_000;

export interface EventFormInput {
  type: string;
  payload: string;
  idempotencyKey: string;
}

export interface EventFormErrors {
  type?: string;
  payload?: string;
  idempotencyKey?: string;
}

export type EventFormResult =
  | { ok: true; value: { type: string; payloadText: string; idempotencyKey: string | undefined } }
  | { ok: false; errors: EventFormErrors };

export function validateEventForm(input: EventFormInput): EventFormResult {
  const errors: EventFormErrors = {};

  const type = input.type.trim();
  if (!type) errors.type = "Enter a type, for example order.created.";
  else if (type.length > MAX_TYPE_LENGTH) errors.type = `The type can be at most ${MAX_TYPE_LENGTH} characters.`;

  const payloadError = checkPayload(input.payload);
  if (payloadError) errors.payload = payloadError;

  const key = input.idempotencyKey.trim();
  if (key.length > MAX_KEY_LENGTH) errors.idempotencyKey = `The key can be at most ${MAX_KEY_LENGTH} characters.`;

  if (Object.keys(errors).length > 0) return { ok: false, errors };

  // The payload text is passed on untouched: the signature is computed over exactly these characters.
  return { ok: true, value: { type, payloadText: input.payload, idempotencyKey: key || undefined } };
}

function checkPayload(text: string): string | null {
  if (!text.trim()) return "Enter the payload as JSON.";
  if (text.length > MAX_PAYLOAD_LENGTH) return `The payload can be at most ${MAX_PAYLOAD_LENGTH} characters.`;

  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return "The payload is not valid JSON.";
  }

  return parsed === null ? "The payload must be a JSON value other than null." : null;
}
