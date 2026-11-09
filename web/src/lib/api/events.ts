import "server-only";
import { apiGet, apiPost, type ApiCallOptions, type ApiResult } from "@/lib/api/client";
import type { Delivery } from "@/lib/api/deliveries";
import { isGuid } from "@/lib/api/ids";

export interface EventDetail {
  id: string;
  type: string;
  createdAt: string;
  /** Whatever the sender put in it. It is only ever shown as text. */
  payload: unknown;
  deliveries: Delivery[];
}

export interface EventAccepted {
  eventId: string;
  deliveries: number;
  duplicate: boolean;
}

export interface NewEvent {
  type: string;
  /** The payload as the user wrote it. It is sent unchanged, because the signature covers these characters. */
  payloadText: string;
  idempotencyKey: string | undefined;
}

const NOT_FOUND = { ok: false, kind: "rejected", status: 404 } as const;
const BAD_REQUEST = { ok: false, kind: "rejected", status: 400 } as const;

export async function getEvent(options: ApiCallOptions, id: string): Promise<ApiResult<EventDetail>> {
  if (!isGuid(id)) return NOT_FOUND;
  return apiGet<EventDetail>(`/events/${id}`, options);
}

export async function createEvent(options: ApiCallOptions, event: NewEvent): Promise<ApiResult<EventAccepted>> {
  // The payload is written into the request as it is, so it has to be exactly one JSON value that is not null.
  // Anything else could add fields of its own to the request. The form checks this too, but this is the
  // place that builds the request, so it does not rely on that.
  if (!isSingleJsonValue(event.payloadText)) return BAD_REQUEST;

  const fields = [`"type":${JSON.stringify(event.type)}`, `"payload":${event.payloadText}`];
  if (event.idempotencyKey !== undefined) fields.push(`"idempotencyKey":${JSON.stringify(event.idempotencyKey)}`);

  return apiPost<EventAccepted>("/events", options, `{${fields.join(",")}}`);
}

function isSingleJsonValue(text: string): boolean {
  try {
    return JSON.parse(text) !== null;
  } catch {
    return false;
  }
}
