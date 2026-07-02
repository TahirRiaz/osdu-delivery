import { runtimeConfig } from "../config/runtime";

/** An RFC 7807 problem from the API, with the correlation id support needs to find the server log line. */
export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail: string | null;
  readonly correlationId: string | null;

  constructor(status: number, title: string, detail: string | null, correlationId: string | null) {
    super(detail ? `${title}: ${detail}` : title);
    this.name = "ApiError";
    this.status = status;
    this.title = title;
    this.detail = detail;
    this.correlationId = correlationId;
  }
}

export function isApiError(error: unknown): error is ApiError {
  return error instanceof ApiError;
}

// ---- Auth token wiring (owned by AuthContext) ---------------------------------------------------------------------

let currentToken: string | null = null;
let onUnauthorized: (() => void) | null = null;

export function setAuthToken(token: string | null): void {
  currentToken = token;
}

/** AuthContext registers the session-expiry handler: called when an authenticated call answers 401. */
export function setUnauthorizedHandler(handler: (() => void) | null): void {
  onUnauthorized = handler;
}

// ---- Rate-limit pause (the 120/min budget) --------------------------------------------------------------------------

type RateLimitListener = (pausedUntilMs: number | null) => void;

let pausedUntilMs: number | null = null;
const rateLimitListeners = new Set<RateLimitListener>();

/** Polling queries consult this: while paused, refetch intervals back off so the budget can recover. */
export function rateLimitPausedUntil(): number | null {
  if (pausedUntilMs !== null && Date.now() >= pausedUntilMs) {
    pausedUntilMs = null;
  }

  return pausedUntilMs;
}

export function subscribeRateLimit(listener: RateLimitListener): () => void {
  rateLimitListeners.add(listener);
  return () => rateLimitListeners.delete(listener);
}

function pauseForRateLimit(retryAfterSeconds: number | null): void {
  const pauseMs = (retryAfterSeconds ?? 30) * 1000;
  pausedUntilMs = Date.now() + pauseMs;
  for (const listener of rateLimitListeners) {
    listener(pausedUntilMs);
  }
  window.setTimeout(() => {
    if (pausedUntilMs !== null && Date.now() >= pausedUntilMs) {
      pausedUntilMs = null;
      for (const listener of rateLimitListeners) {
        listener(null);
      }
    }
  }, pauseMs + 50);
}

// ---- Requests ------------------------------------------------------------------------------------------------------------

export type QueryParams = Record<string, string | number | boolean | null | undefined>;

interface RequestOptions {
  method: string;
  path: string;
  query?: QueryParams;
  body?: unknown;
  signal?: AbortSignal;
  /** Sign-in calls set this: a 401 is the expected "wrong credentials" answer, not an expired session. */
  anonymous?: boolean;
}

async function request<T>(options: RequestOptions): Promise<T> {
  const base = runtimeConfig().apiBaseUrl;
  const url = new URL(`${base}${options.path}`);
  if (options.query) {
    for (const [key, value] of Object.entries(options.query)) {
      if (value !== null && value !== undefined && value !== "") {
        url.searchParams.set(key, String(value));
      }
    }
  }

  const headers: Record<string, string> = { Accept: "application/json" };
  if (!options.anonymous && currentToken) {
    headers.Authorization = `Bearer ${currentToken}`;
  }
  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
  }

  let response: Response;
  try {
    response = await fetch(url, {
      method: options.method,
      headers,
      body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
      signal: options.signal ?? null,
    });
  } catch (error) {
    if (error instanceof DOMException && error.name === "AbortError") {
      throw error;
    }

    throw new ApiError(0, "Control plane unreachable",
      "The API did not answer; check that the control plane is running and CORS allows this origin.", null);
  }

  if (response.status === 401 && !options.anonymous && currentToken) {
    onUnauthorized?.();
  }

  if (response.status === 429) {
    const retryAfter = response.headers.get("Retry-After");
    pauseForRateLimit(retryAfter ? Number.parseInt(retryAfter, 10) || null : null);
  }

  if (!response.ok) {
    throw await toApiError(response);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

async function toApiError(response: Response): Promise<ApiError> {
  let title = `HTTP ${response.status}`;
  let detail: string | null = null;
  let correlationId = response.headers.get("X-Correlation-ID");
  const contentType = response.headers.get("Content-Type") ?? "";
  if (contentType.includes("json")) {
    try {
      const problem = (await response.json()) as {
        title?: string;
        detail?: string;
        correlationId?: string;
        extensions?: { correlationId?: string };
      };
      title = problem.title ?? title;
      detail = problem.detail ?? null;
      correlationId = problem.correlationId ?? problem.extensions?.correlationId ?? correlationId;
    } catch {
      // A non-JSON error body (proxy page, empty body): the status-derived title stands.
    }
  }

  return new ApiError(response.status, title, detail, correlationId);
}

export function get<T>(path: string, query?: QueryParams, signal?: AbortSignal): Promise<T> {
  return request<T>({ method: "GET", path, query, signal });
}

export function post<T>(path: string, body?: unknown): Promise<T> {
  return request<T>({ method: "POST", path, body });
}

export function del<T>(path: string): Promise<T> {
  return request<T>({ method: "DELETE", path });
}

export function postAnonymous<T>(path: string, body: unknown): Promise<T> {
  return request<T>({ method: "POST", path, body, anonymous: true });
}

export function getAnonymous<T>(path: string): Promise<T> {
  return request<T>({ method: "GET", path, anonymous: true });
}
