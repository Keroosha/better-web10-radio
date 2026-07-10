// Typed fetch wrapper for the Web10.Radio HTTP API.
//
// Contract rules enforced here (SPEC §5/§10):
//   • Every successful body is validated with a Zod schema before it is returned.
//   • Every error body is parsed as RFC 7807 problem details and surfaced as a
//     typed `ApiError`.
//   • Admin requests use the HttpOnly session cookie; mutations synchronize the
//     recoverable CSRF token held only in module memory.
import { z } from 'zod';

import type { AdminSession } from '../domain/admin';
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

let adminSession: AdminSession | null = null;
type AdminSessionInvalidationListener = () => void;
const adminSessionInvalidationListeners = new Set<AdminSessionInvalidationListener>();

/** Retain the validated login/session DTO needed to synchronize admin mutations. */
export function setAdminSession(session: AdminSession): void {
  adminSession = session;
}

/** Clear locally retained admin session state without notifying application subscribers. */
export function clearAdminSession(): void {
  adminSession = null;
}

/** Subscribe to server-driven authenticated-session expiry or revocation. */
export function subscribeToAdminSessionInvalidation(
  listener: AdminSessionInvalidationListener,
): () => void {
  adminSessionInvalidationListeners.add(listener);
  return (): void => {
    adminSessionInvalidationListeners.delete(listener);
  };
}

function invalidateAdminSession(): void {
  clearAdminSession();
  for (const listener of adminSessionInvalidationListeners) {
    listener();
  }
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

interface AdminRequestContext {
  /** Marks an admin route: include cookies and apply CSRF when appropriate. */
  readonly admin?: boolean;
  /** Login/session probe: never invalidates state or synchronizes CSRF. */
  readonly authProbe?: boolean;
}

export interface ApiRequest<TResponse, TBody extends object = never> extends AdminRequestContext {
  /** Schema the response body is validated against. */
  readonly schema: z.ZodType<TResponse>;
  /** HTTP method; defaults to `GET`. */
  readonly method?: string;
  /** JSON body for a response-carrying mutation. */
  readonly body?: TBody;
  readonly signal?: AbortSignal;
  /** Injected `fetch` for tests; defaults to the global. */
  readonly fetchImpl?: FetchImpl;
}

/** A body-carrying request whose successful response has no JSON body (e.g. `204`). */
export interface SendRequest<TBody extends object> extends AdminRequestContext {
  /** HTTP method; there is no default (a send always mutates). */
  readonly method: string;
  /** JSON body; when present it is sent as `application/json`. */
  readonly body?: TBody;
  readonly signal?: AbortSignal;
  /** Injected `fetch` for tests; defaults to the global. */
  readonly fetchImpl?: FetchImpl;
}


function createHeaders(
  request: AdminRequestContext,
  method: string,
  includesJsonBody: boolean,
): Record<string, string> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (includesJsonBody) {
    headers['Content-Type'] = 'application/json';
  }
  if (
    request.admin === true &&
    request.authProbe !== true &&
    (method === 'POST' || method === 'PUT' || method === 'PATCH' || method === 'DELETE') &&
    adminSession !== null
  ) {
    headers['X-CSRF-Token'] = adminSession.csrfToken;
  }
  return headers;
}

function createRequestInit<TBody extends object>(
  request: AdminRequestContext & { readonly signal?: AbortSignal; readonly body?: TBody },
  method: string,
): RequestInit {
  return {
    method,
    headers: createHeaders(request, method, request.body !== undefined),
    ...(request.admin === true ? { credentials: 'include' as const } : {}),
    ...(request.body !== undefined ? { body: JSON.stringify(request.body) } : {}),
    ...(request.signal ? { signal: request.signal } : {}),
  };
}

async function toApiError(res: Response): Promise<ApiError> {
  const raw = await res.json().catch(() => null);
  const parsed = ProblemDetailsSchema.safeParse(raw);
  const problem = parsed.success ? parsed.data : null;
  const message = problem?.message ?? problem?.title ?? `HTTP ${res.status}`;
  return new ApiError(res.status, message, problem);
}

async function throwForError(res: Response, request: AdminRequestContext): Promise<never> {
  const error = await toApiError(res);
  if (res.status === 401 && request.admin === true && request.authProbe !== true) {
    invalidateAdminSession();
  }
  throw error;
}

/** Perform a request and return the validated successful JSON body. */
export async function apiFetch<TResponse, TBody extends object = never>(
  path: string,
  req: ApiRequest<TResponse, TBody>,
): Promise<TResponse> {
  const doFetch = req.fetchImpl ?? fetch;
  const method = req.method ?? 'GET';
  const res = await doFetch(`${apiBaseUrl}${path}`, createRequestInit(req, method));
  if (!res.ok) {
    return throwForError(res, req);
  }
  return req.schema.parse(await res.json());
}

/** Perform a request that returns no body on success (such as `204`). */
export async function apiSend<TBody extends object>(
  path: string,
  req: SendRequest<TBody>,
): Promise<void> {
  const doFetch = req.fetchImpl ?? fetch;
  const res = await doFetch(`${apiBaseUrl}${path}`, createRequestInit(req, req.method));
  if (!res.ok) {
    return throwForError(res, req);
  }
}
