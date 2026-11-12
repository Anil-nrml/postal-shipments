/**
 * Thin fetch wrapper shared by every micro-frontend.
 *
 * - Auth is cookie based: the access/refresh tokens are HttpOnly cookies the JS never sees.
 * - State-changing requests echo the readable `csrf_token` cookie in `X-CSRF-Token` (double submit).
 * - A 401 triggers one silent refresh (deduplicated across concurrent calls) and a single replay.
 *   Replaying is safe: a 401 means the request was rejected before it executed, and shipment
 *   creation additionally carries an Idempotency-Key.
 * - Errors are normalised from RFC 7807 ProblemDetails into ApiError.
 */
import { emitSessionExpired } from './events';

export const API_BASE = '/api';
const CSRF_COOKIE = 'csrf_token';
const CSRF_HEADER = 'X-CSRF-Token';

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly code?: string,
    public readonly fieldErrors: Record<string, string[]> = {},
    public readonly correlationId?: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }

  get isConflict() {
    return this.status === 409;
  }

  get isUnavailable() {
    return this.status === 503;
  }
}

interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
  code?: string;
  correlationId?: string;
  errors?: Record<string, string[]>;
}

export function readCookie(name: string): string | undefined {
  return document.cookie
    .split(';')
    .map((c) => c.trim())
    .find((c) => c.startsWith(`${name}=`))
    ?.substring(name.length + 1);
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  body?: unknown;
  headers?: Record<string, string>;
  signal?: AbortSignal;
  /** Internal: prevents refresh loops. */
  skipRefresh?: boolean;
}

let refreshInFlight: Promise<boolean> | null = null;

async function refreshSession(): Promise<boolean> {
  refreshInFlight ??= send('/auth/refresh', { method: 'POST', skipRefresh: true })
    .then((r) => r.ok)
    .catch(() => false)
    .finally(() => {
      refreshInFlight = null;
    });
  return refreshInFlight;
}

async function send(path: string, options: RequestOptions): Promise<Response> {
  const method = options.method ?? 'GET';
  const headers: Record<string, string> = { Accept: 'application/json', ...options.headers };
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';
  if (method !== 'GET') {
    const csrf = readCookie(CSRF_COOKIE);
    if (csrf) headers[CSRF_HEADER] = decodeURIComponent(csrf);
  }
  return fetch(`${API_BASE}${path}`, {
    method,
    headers,
    credentials: 'same-origin',
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    signal: options.signal,
  });
}

async function toApiError(response: Response): Promise<ApiError> {
  let problem: ProblemDetails = {};
  try {
    problem = (await response.json()) as ProblemDetails;
  } catch {
    /* non-JSON error body */
  }
  const message =
    problem.detail ??
    problem.title ??
    (response.status === 0 ? 'Network error' : `Request failed (${response.status})`);
  return new ApiError(response.status, message, problem.code, problem.errors ?? {}, problem.correlationId);
}

export async function apiRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  let response: Response;
  try {
    response = await send(path, options);
  } catch (e) {
    if ((e as Error).name === 'AbortError') throw e;
    throw new ApiError(0, 'Cannot reach the server. Check your connection and try again.');
  }

  if (response.status === 401 && !options.skipRefresh && !path.startsWith('/auth/')) {
    if (await refreshSession()) return apiRequest<T>(path, { ...options, skipRefresh: true });
    emitSessionExpired();
  }

  if (!response.ok) throw await toApiError(response);
  if (response.status === 204) return undefined as T;
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const api = {
  get: <T>(path: string, signal?: AbortSignal) => apiRequest<T>(path, { signal }),
  post: <T>(path: string, body?: unknown, headers?: Record<string, string>) =>
    apiRequest<T>(path, { method: 'POST', body, headers }),
  put: <T>(path: string, body: unknown) => apiRequest<T>(path, { method: 'PUT', body }),
  patch: <T>(path: string, body: unknown) => apiRequest<T>(path, { method: 'PATCH', body }),
  delete: (path: string) => apiRequest<void>(path, { method: 'DELETE' }),
};

/** Builds a query string, skipping empty values. */
export function toQuery(params: Record<string, string | number | boolean | null | undefined>): string {
  const search = new URLSearchParams();
  Object.entries(params).forEach(([key, value]) => {
    if (value !== undefined && value !== null && value !== '') search.set(key, String(value));
  });
  const qs = search.toString();
  return qs ? `?${qs}` : '';
}

/** User-facing message for any error thrown by a query or mutation. */
export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 403) return 'You do not have permission to do that.';
    if (error.status === 429) return 'Too many requests. Please wait a moment and retry.';
    return error.message;
  }
  return error instanceof Error ? error.message : 'Something went wrong.';
}
