import "server-only";
import { apiGet, apiPost, type ApiCallOptions, type ApiResult } from "@/lib/api/client";
import { isGuid } from "@/lib/api/ids";

export const STATUSES = ["Pending", "InProgress", "Succeeded", "Dead"] as const;
export type DeliveryStatus = (typeof STATUSES)[number];

export interface Delivery {
  id: string;
  eventId: string;
  eventType: string;
  endpointId: string;
  endpointUrl: string;
  /** The API's own word. It is typed as the four known values but is checked where it is shown. */
  status: string;
  attemptCount: number;
  nextAttemptAt: string;
  lastError: string | null;
  createdAt: string;
}

export interface Attempt {
  id: string;
  startedAt: string;
  durationMs: number;
  responseStatus: number | null;
  responseSnippet: string | null;
  error: string | null;
}

export interface DeliveryDetail {
  delivery: Delivery;
  attempts: Attempt[];
}

const LIST_LIMIT = 100;
const NOT_FOUND = { ok: false, kind: "rejected", status: 404 } as const;

/** The newest deliveries, optionally only those in one status. An unknown status is ignored, never passed on. */
export function listDeliveries(options: ApiCallOptions, status?: string): Promise<ApiResult<Delivery[]>> {
  const known = STATUSES.find((s) => s === status);
  const query = known ? `limit=${LIST_LIMIT}&status=${known}` : `limit=${LIST_LIMIT}`;
  return apiGet<Delivery[]>(`/deliveries?${query}`, options);
}

export async function getDelivery(options: ApiCallOptions, id: string): Promise<ApiResult<DeliveryDetail>> {
  // The id comes from the address bar. Only a real id is allowed to become part of a path to the API.
  if (!isGuid(id)) return NOT_FOUND;
  return apiGet<DeliveryDetail>(`/deliveries/${id}`, options);
}

export async function resendDelivery(options: ApiCallOptions, id: string): Promise<ApiResult<Delivery>> {
  if (!isGuid(id)) return NOT_FOUND;
  return apiPost<Delivery>(`/deliveries/${id}/resend`, options);
}
