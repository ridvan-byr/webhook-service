import "server-only";

export type ApiResult<T> =
  | { ok: true; data: T }
  | { ok: false; kind: "unauthorized" | "rejected" | "server"; status: number }
  | { ok: false; kind: "unreachable"; status?: undefined };

export interface ApiCallOptions {
  baseUrl: string;
  apiKey: string;
  fetch?: typeof fetch;
  timeoutMs?: number;
}

const DEFAULT_TIMEOUT_MS = 10_000;

// A path may only continue the API's own address: one leading slash, no second host, no whitespace or
// line breaks, no backslashes. Anything else could send the request (and the key) somewhere else.
const SAFE_PATH = /^\/(?![/\\])[^\s\\@]*$/;

/**
 * Asks the API for something on behalf of the signed-in user.
 *
 * The result never carries the key or anything the API wrote: the caller learns only what kind of problem
 * it was, so nothing sensitive can reach a page or a log by accident.
 */
export function apiGet<T>(path: string, options: ApiCallOptions): Promise<ApiResult<T>> {
  return request<T>("GET", path, options);
}

/** Asks the API to do something, optionally with a JSON body already written as text. Same guarantees as apiGet. */
export function apiPost<T>(path: string, options: ApiCallOptions, body?: string): Promise<ApiResult<T>> {
  return request<T>("POST", path, options, body);
}

async function request<T>(
  method: "GET" | "POST",
  path: string,
  options: ApiCallOptions,
  body?: string,
): Promise<ApiResult<T>> {
  if (!SAFE_PATH.test(path)) throw new Error("Invalid API path.");

  const fetchImpl = options.fetch ?? fetch;

  let response: Response;
  try {
    response = await fetchImpl(`${options.baseUrl}${path}`, {
      method,
      headers: {
        Authorization: `Bearer ${options.apiKey}`,
        Accept: "application/json",
        ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      },
      body,
      cache: "no-store",
      redirect: "manual",
      signal: AbortSignal.timeout(options.timeoutMs ?? DEFAULT_TIMEOUT_MS),
    });
  } catch {
    return { ok: false, kind: "unreachable" };
  }

  if (response.status === 401) return { ok: false, kind: "unauthorized", status: 401 };
  if (response.status >= 400 && response.status < 500) return { ok: false, kind: "rejected", status: response.status };
  if (response.status < 200 || response.status >= 300) return { ok: false, kind: "server", status: response.status };

  try {
    return { ok: true, data: (await response.json()) as T };
  } catch {
    return { ok: false, kind: "server", status: response.status };
  }
}
