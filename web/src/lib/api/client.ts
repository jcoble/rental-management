/**
 * Core browser-side API client for RentalCommand.
 *
 * Mirrors EdiPlatform's fetch wrapper:
 *  - attaches the bearer token from the runes auth store,
 *  - proactively refreshes a near-expired token,
 *  - on 401, refreshes via the same-origin /api/auth/refresh proxy and retries
 *    the request once,
 *  - parses the API's error bodies (plain { error }, StandardErrorResponse, and
 *    ASP.NET ValidationProblemDetails) into a structured {@link ApiError},
 *  - throws on any non-2xx.
 *
 * The refresh-token cookie is httpOnly and first-party to the SvelteKit origin,
 * so refreshes must go through our own /api/auth/refresh endpoint (same origin)
 * rather than calling the API directly.
 */

import { browser } from '$app/environment';
import { CLIENT_API_BASE_URL } from '$lib/config';
import { getAuthState, updateToken, clearAuth, isTokenExpired } from '$lib/stores/auth.svelte';
import { createSingleFlight } from '$lib/utils/single-flight';

export const API_BASE_URL = CLIENT_API_BASE_URL;

const REFRESH_FETCH_TIMEOUT_MS = 10_000;
const API_FETCH_TIMEOUT_MS = 20_000;

/** Structured error mirroring a StandardErrorResponse entry. */
export interface StandardError {
	code?: string;
	message: string;
	title?: string;
	field?: string;
	severity?: string;
	isRetryable?: boolean;
}

export class ApiError extends Error {
	constructor(
		public status: number,
		message: string,
		public code?: string,
		public error?: StandardError,
		public errors?: StandardError[],
		public details?: string[],
		/** ASP.NET ValidationProblemDetails: { "Field": ["message"] } */
		public validationErrors?: Record<string, string[]>
	) {
		super(message);
		this.name = 'ApiError';
	}
}

interface FetchOptions extends RequestInit {
	requireAuth?: boolean;
	timeoutMs?: number;
}

async function fetchWithTimeout(
	input: RequestInfo | URL,
	init: RequestInit,
	timeoutMs = API_FETCH_TIMEOUT_MS
): Promise<Response> {
	const controller = new AbortController();
	const timeout = setTimeout(() => controller.abort(), timeoutMs);
	try {
		return await fetch(input, { ...init, signal: controller.signal });
	} finally {
		clearTimeout(timeout);
	}
}

async function parseJsonOrEmpty<T>(response: Response): Promise<T> {
	if (response.status === 204 || response.status === 205) {
		return undefined as T;
	}
	const body = await response.text();
	if (!body.trim()) {
		return undefined as T;
	}
	return JSON.parse(body) as T;
}

function fallbackHttpErrorMessage(response: Response): string {
	if (response.status === 429) {
		const retryAfter = response.headers.get('retry-after');
		const retryText = retryAfter ? ` Try again in ${retryAfter} seconds.` : ' Try again shortly.';
		return `Too many requests.${retryText}`;
	}
	if (response.status === 401) return 'Session expired. Please sign in again.';
	if (response.status === 403) return 'You do not have permission to perform this action.';
	if (response.status === 404) return 'The requested resource was not found.';
	if (response.status >= 500) return 'The server hit an error. Please try again.';
	return `Request failed (${response.status})`;
}

/**
 * Refresh the access token via the same-origin proxy endpoint, then update the
 * client auth store. Deduplicated so concurrent 401s share one refresh.
 */
async function performTokenRefresh(): Promise<void> {
	const controller = new AbortController();
	const timeout = setTimeout(() => controller.abort(), REFRESH_FETCH_TIMEOUT_MS);
	const response = await fetch('/api/auth/refresh', {
		method: 'POST',
		credentials: 'include',
		signal: controller.signal
	}).finally(() => clearTimeout(timeout));

	if (!response.ok) {
		throw new ApiError(response.status, 'Token refresh failed');
	}

	const data = await response.json();
	updateToken(data.accessToken, new Date(data.accessTokenExpiration));
}

export const refreshToken = createSingleFlight(performTokenRefresh);

function buildErrorFromBody(response: Response, errorData: unknown): ApiError {
	const body = (errorData ?? {}) as Record<string, unknown>;

	// StandardErrorResponse: { error: { code, message, ... }, errors: [...] }
	const primaryError: StandardError | undefined =
		body.error && typeof body.error === 'object'
			? (body.error as StandardError)
			: undefined;
	const errors: StandardError[] | undefined = Array.isArray(body.errors)
		? (body.errors as StandardError[])
		: undefined;
	// ASP.NET ValidationProblemDetails: { title, status, errors: { Field: [msg] } }
	const validationErrors: Record<string, string[]> | undefined =
		body.errors && !Array.isArray(body.errors) && typeof body.errors === 'object'
			? (body.errors as Record<string, string[]>)
			: undefined;

	const message =
		primaryError?.message ??
		(typeof body.error === 'string' ? body.error : null) ??
		(typeof body.title === 'string' ? body.title : null) ??
		(typeof body.message === 'string' ? body.message : null) ??
		fallbackHttpErrorMessage(response);

	return new ApiError(
		response.status,
		message,
		primaryError?.code,
		primaryError,
		errors,
		Array.isArray(body.details) ? (body.details as string[]) : undefined,
		validationErrors
	);
}

/** Low-level authenticated fetch. Throws {@link ApiError} on non-2xx. */
export async function fetchApi<T>(endpoint: string, options: FetchOptions = {}): Promise<T> {
	const { requireAuth = true, timeoutMs = API_FETCH_TIMEOUT_MS, ...fetchOptions } = options;
	const headers: Record<string, string> = {
		...(fetchOptions.headers as Record<string, string>)
	};

	if (!(fetchOptions.body instanceof FormData)) {
		headers['Content-Type'] = 'application/json';
	}

	if (requireAuth) {
		// Refresh BEFORE sending when the token is MISSING or expires within 120s. isTokenExpired()
		// returns true when the in-memory token/expiration is absent too — e.g. a query/mutation that
		// fires before hydration, or after idling on one page. The previous guard (`&& auth.accessToken`)
		// skipped this whenever the token was momentarily falsy, so the request went out UNAUTHENTICATED
		// and 401'd; for a multipart scan upload that meant re-uploading the whole file on the retry.
		if (browser && isTokenExpired(120)) {
			try {
				await refreshToken();
			} catch {
				// Proactive refresh failed — proceed; the 401 retry below still covers it.
			}
		}
		const currentAuth = getAuthState();
		if (currentAuth.accessToken) {
			headers['Authorization'] = `Bearer ${currentAuth.accessToken}`;
		}
	}

	let response: Response;
	try {
		response = await fetchWithTimeout(
			`${API_BASE_URL}${endpoint}`,
			{ ...fetchOptions, headers, credentials: 'include' },
			timeoutMs
		);
	} catch (error: unknown) {
		if (error instanceof DOMException && error.name === 'AbortError') {
			throw new ApiError(408, 'The request is taking longer than expected. Try again in a moment.');
		}
		throw error;
	}

	if (!response.ok) {
		if (response.status === 401 && browser) {
			// Refresh and retry once.
			try {
				await refreshToken();
				const retryAuth = getAuthState();
				if (retryAuth.accessToken) {
					headers['Authorization'] = `Bearer ${retryAuth.accessToken}`;
					const retryResponse = await fetchWithTimeout(
						`${API_BASE_URL}${endpoint}`,
						{ ...fetchOptions, headers, credentials: 'include' },
						timeoutMs
					);
					if (retryResponse.ok) {
						return parseJsonOrEmpty<T>(retryResponse);
					}
				}
			} catch {
				// fall through to clear auth
			}
			clearAuth();
			throw new ApiError(401, 'Session expired. Please sign in again.');
		}

		const errorData = await response.json().catch(() => ({}));
		throw buildErrorFromBody(response, errorData);
	}

	return parseJsonOrEmpty<T>(response);
}

/** Fetch wrapper that doesn't attach/refresh auth (public endpoints). */
export async function fetchPublicApi<T>(endpoint: string, options: RequestInit = {}): Promise<T> {
	return fetchApi<T>(endpoint, { ...options, requireAuth: false });
}

/**
 * Backwards-compatible convenience client used by the existing endpoint
 * modules (`api.get('/portfolios')`, etc.). Routes through {@link fetchApi}
 * so they inherit auth, refresh-on-401, and structured errors. Endpoint paths
 * are relative to {@link API_BASE_URL} (`/api/v1`).
 */
export const api = {
	get: <T>(path: string) => fetchApi<T>(path),
	post: <T>(path: string, data?: unknown) =>
		fetchApi<T>(path, { method: 'POST', body: data ? JSON.stringify(data) : undefined }),
	patch: <T>(path: string, data: unknown) =>
		fetchApi<T>(path, { method: 'PATCH', body: JSON.stringify(data) }),
	put: <T>(path: string, data: unknown) =>
		fetchApi<T>(path, { method: 'PUT', body: JSON.stringify(data) }),
	delete: <T = void>(path: string) => fetchApi<T>(path, { method: 'DELETE' }),
	upload: <T>(path: string, formData: FormData) =>
		fetchApi<T>(path, { method: 'POST', body: formData })
};
