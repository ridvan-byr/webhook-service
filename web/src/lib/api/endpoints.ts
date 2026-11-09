import "server-only";
import { apiGet, apiPatch, apiPost, type ApiCallOptions, type ApiResult } from "@/lib/api/client";
import { isGuid } from "@/lib/api/ids";

export interface Endpoint {
  id: string;
  url: string;
  isActive: boolean;
  createdAt: string;
}

export interface CreatedEndpoint extends Endpoint {
  /** Shown once, in the response that creates the endpoint. The API never returns it again. */
  signingSecret: string;
}

const NOT_FOUND = { ok: false, kind: "rejected", status: 404 } as const;
const BAD_REQUEST = { ok: false, kind: "rejected", status: 400 } as const;

export function listEndpoints(options: ApiCallOptions): Promise<ApiResult<Endpoint[]>> {
  return apiGet<Endpoint[]>("/endpoints", options);
}

export function createEndpoint(options: ApiCallOptions, url: string): Promise<ApiResult<CreatedEndpoint>> {
  return apiPost<CreatedEndpoint>("/endpoints", options, JSON.stringify({ url }));
}

export async function setEndpointActive(
  options: ApiCallOptions,
  id: string,
  isActive: boolean,
): Promise<ApiResult<Endpoint>> {
  // The id comes from the address bar or a form. Only a real id becomes part of a path to the API.
  if (!isGuid(id)) return NOT_FOUND;
  // A form can send anything; only exactly true or false is passed on.
  if (typeof isActive !== "boolean") return BAD_REQUEST;

  return apiPatch<Endpoint>(`/endpoints/${id}`, options, JSON.stringify({ isActive }));
}
