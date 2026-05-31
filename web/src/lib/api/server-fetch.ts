/**
 * Server-side authenticated fetch for +page.server.ts / +server.ts loaders.
 *
 * Uses the absolute SERVER_API_BASE_URL and an explicit access token
 * (from event.locals.accessToken). Returns a discriminated result rather than
 * throwing, so loaders can render partial pages on a single failed call.
 */

import { SERVER_API_BASE_URL } from '$lib/server/config';

const SERVER_FETCH_TIMEOUT_MS = 20_000;

export interface ServerFetchOptions extends RequestInit {
	accessToken: string;
}

export interface ServerFetchResult<T> {
	data: T | null;
	error: string | null;
	status: number;
	validationErrors?: Record<string, string[]>;
}

async function fetchWithTimeout(input: RequestInfo | URL, init: RequestInit): Promise<Response> {
	const controller = new AbortController();
	const timeout = setTimeout(() => controller.abort(), SERVER_FETCH_TIMEOUT_MS);
	try {
		return await fetch(input, { ...init, signal: controller.signal });
	} finally {
		clearTimeout(timeout);
	}
}

function fallbackHttpErrorMessage(status: number): string {
	if (status === 401) return 'Session expired. Please sign in again.';
	if (status === 403) return 'You do not have permission to perform this action.';
	if (status === 404) return 'The requested resource was not found.';
	if (status >= 500) return 'The server hit an error. Please try again.';
	return `Request failed (${status})`;
}

export async function serverFetch<T>(
	endpoint: string,
	options: ServerFetchOptions
): Promise<ServerFetchResult<T>> {
	const { accessToken, ...fetchOptions } = options;

	const headers: Record<string, string> = {
		Authorization: `Bearer ${accessToken}`,
		'Content-Type': 'application/json',
		...(fetchOptions.headers as Record<string, string>)
	};

	try {
		const response = await fetchWithTimeout(`${SERVER_API_BASE_URL}${endpoint}`, {
			...fetchOptions,
			headers
		});

		if (!response.ok) {
			const errorData = await response.json().catch(() => ({}));
			const rawError = errorData.error;
			const errorMessage =
				typeof rawError === 'string'
					? rawError
					: rawError?.title ||
						rawError?.message ||
						errorData.message ||
						errorData.title ||
						fallbackHttpErrorMessage(response.status);

			const validationErrors: Record<string, string[]> | undefined =
				errorData.errors && !Array.isArray(errorData.errors) && typeof errorData.errors === 'object'
					? errorData.errors
					: undefined;

			return {
				data: null,
				error: validationErrors ? 'Please fix the validation errors below.' : errorMessage,
				status: response.status,
				validationErrors
			};
		}

		const data =
			response.status === 204 || response.status === 205
				? (null as T | null)
				: ((await response.text().then((t) => (t.trim() ? JSON.parse(t) : null))) as T | null);
		return { data, error: null, status: response.status };
	} catch (err) {
		console.error(`Server fetch error for ${endpoint}:`, err);
		return {
			data: null,
			error: err instanceof Error ? err.message : 'Network error',
			status: 500
		};
	}
}

export function serverGet<T>(endpoint: string, accessToken: string): Promise<ServerFetchResult<T>> {
	return serverFetch<T>(endpoint, { accessToken, method: 'GET' });
}

export function serverPost<T>(
	endpoint: string,
	accessToken: string,
	body?: unknown
): Promise<ServerFetchResult<T>> {
	return serverFetch<T>(endpoint, {
		accessToken,
		method: 'POST',
		body: body ? JSON.stringify(body) : undefined
	});
}
