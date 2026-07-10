// Typed fetch wrapper for the Web10.Radio HTTP API.
//
// Contract rules enforced here (SPEC §5/§10):
//   • Every successful body is validated with a Zod schema before it is returned,
//     so callers never touch an untyped payload and no domain-erasing cast is used.
//   • Every error body is parsed as RFC 7807 problem details and surfaced as a
//     typed `ApiError`.
//
// Base URL: `shared` stays free of build-tool/env coupling. The default is the
// empty string, so requests are same-origin relative paths (`/api/v0/...`) — which
// the Vite dev proxy forwards to the backend in development and which resolve
// directly in production. An app may override it once at startup via `setApiBaseUrl`.
import { z } from 'zod';

import { ProblemDetailsSchema, type ProblemDetails } from '../domain/problem-details';

/** Shared route prefix for all v0 endpoints (SPEC §5). */
export const API_V0_PREFIX = '/api/v0';

let apiBaseUrl = '';

/** Override the API origin (e.g. an absolute URL). Trailing slash is trimmed. */
export function setApiBaseUrl(base: string): void {
  apiBaseUrl = base.replace(/\/+$/, '');
}

/** The currently configured API origin (empty string = same-origin). */
export function getApiBaseUrl(): string {
  return apiBaseUrl;
}

// Admin routes (`/api/v0/admin/*`) require `Authorization: Bearer <WEB10_ADMIN__TOKEN>`
// (backend `Web10Admin` policy). Player routes are unauthenticated and must stay
// header-free. The token is app-configured once at startup (like the base URL); only
// requests that opt in with `admin: true` carry it.
let adminToken: string | null = null;

/** Set (or clear, with `null`) the admin bearer token attached to admin requests. */
export function setAdminToken(token: string | null): void {
  adminToken = token;
}

/** The currently configured admin bearer token (`null` = unset). */
export function getAdminToken(): string | null {
  return adminToken;
}

/** The `fetch` surface we depend on; injectable so tests need no real network. */
export type FetchImpl = (input: string, init?: RequestInit) => Promise<Response>;

/** Per-call options shared by the typed endpoint helpers. */
export interface RequestOptions {
  readonly signal?: AbortSignal;
  readonly fetchImpl?: FetchImpl;
}

/** A typed transport error carrying the parsed RFC 7807 body when available. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string | null;
  readonly traceId: string | null;
  readonly problem: ProblemDetails | null;

  constructor(status: number, message: string, problem: ProblemDetails | null) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = problem?.code ?? null;
    this.traceId = problem?.traceId ?? null;
    this.problem = problem;
  }
}

export interface ApiRequest<T> {
  /** Schema the response body is validated against. */
  readonly schema: z.ZodType<T>;
  /** HTTP method; defaults to `GET`. */
  readonly method?: string;
  readonly signal?: AbortSignal;
  /** Injected `fetch` for tests; defaults to the global. */
  readonly fetchImpl?: FetchImpl;
  /** When true, attach `Authorization: Bearer <admin token>` (admin routes only). */
  readonly admin?: boolean;
}

async function toApiError(res: Response): Promise<ApiError> {
  const raw = await res.json().catch(() => null);
  const parsed = ProblemDetailsSchema.safeParse(raw);
  const problem = parsed.success ? parsed.data : null;
  const message = problem?.message ?? problem?.title ?? `HTTP ${res.status}`;
  return new ApiError(res.status, message, problem);
}

/**
 * Perform a request and return the validated body. Throws {@link ApiError} on any
 * non-2xx response and lets `ZodError` propagate if a 2xx body violates its schema
 * (a contract breach worth surfacing, not swallowing).
 */
export async function apiFetch<T>(path: string, req: ApiRequest<T>): Promise<T> {
  const doFetch = req.fetchImpl ?? fetch;
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (req.admin === true && adminToken !== null) {
    headers.Authorization = `Bearer ${adminToken}`;
  }
  const init: RequestInit = {
    method: req.method ?? 'GET',
    headers,
    ...(req.signal ? { signal: req.signal } : {}),
  };
  const res = await doFetch(`${apiBaseUrl}${path}`, init);
  if (!res.ok) {
    throw await toApiError(res);
  }
  return req.schema.parse(await res.json());
}
