/**
 * Server hooks for authentication.
 *
 * On every request we validate the app-namespaced access-token cookie against the API
 * (GET /auth/access), refreshing on 401, and populate event.locals.user,
 * event.locals.access, and event.locals.accessToken for downstream load functions and guards.
 *
 * Mirrors EdiPlatform's hooks.server.ts, adapted to RentalCommand's int user
 * keys and rental routes (no Sentry dependency here).
 */

import type { Handle, HandleFetch } from '@sveltejs/kit';
import type { Cookies } from '@sveltejs/kit';
import { env } from '$env/dynamic/public';
import type { AccessEnvelope, User } from '$lib/types/user';
import { userFromAccessEnvelope } from '$lib/types/user';
import { serverRefreshToken, applyRefreshCookies } from '$lib/server/token-refresh';
import { SERVER_API_BASE_URL } from '$lib/server/config';
import { SIM_CLOCK_SHIM_PLACEHOLDER, simClockShimScript } from '$lib/dev/sim-clock-shim';
import {
	deleteAccessCookies,
	getAccessToken,
	getAccessTokenExpiration,
	getRefreshToken
} from '$lib/server/auth-cookies';

const API_URL = new URL(SERVER_API_BASE_URL);
const AUTH_FETCH_TIMEOUT_MS = 10_000;

async function fetchWithAuthTimeout(input: string, init: RequestInit): Promise<Response> {
	const controller = new AbortController();
	const timeout = setTimeout(() => controller.abort(), AUTH_FETCH_TIMEOUT_MS);
	try {
		return await fetch(input, { ...init, signal: controller.signal });
	} finally {
		clearTimeout(timeout);
	}
}

export const handle: Handle = async ({ event, resolve }) => {
	event.locals.user = null;
	event.locals.accessToken = null;
	event.locals.access = null;

	const accessToken = getAccessToken(event.cookies);

	if (accessToken) {
		try {
			const response = await fetchWithAuthTimeout(`${SERVER_API_BASE_URL}/auth/access`, {
				headers: { Authorization: `Bearer ${accessToken}` }
			});

			if (response.ok) {
				const access: AccessEnvelope = await response.json();
				event.locals.access = access;
				event.locals.user = userFromAccessEnvelope(access);
				event.locals.accessToken = accessToken;
				const storedExpiration = getAccessTokenExpiration(event.cookies);
				if (storedExpiration) {
					event.locals.accessTokenExpiration = storedExpiration;
				}
			} else if (response.status === 401) {
				// Access token expired — try a refresh.
				const refreshed = await tryRefreshToken(event.cookies);
				if (refreshed) {
					event.locals.user = refreshed.user;
					event.locals.access = refreshed.access;
					event.locals.accessToken = refreshed.accessToken;
					event.locals.accessTokenExpiration = refreshed.accessTokenExpiration;
				} else {
					deleteAccessCookies(event.cookies);
				}
			} else {
				// The canonical bearer contains only authority coordinates, never display or workspace
				// facts. Keep the cookies for a later retry but fail this request closed.
				console.warn(
					`Auth /access returned ${response.status} — canonical session could not be validated`
				);
			}
		} catch (error) {
			console.warn('Auth validation error; canonical session could not be validated:', error);
		}
	} else {
		// No access token — fall back to the refresh token if present.
		const refreshToken = getRefreshToken(event.cookies);
		if (refreshToken) {
			const refreshed = await tryRefreshToken(event.cookies);
			if (refreshed) {
				event.locals.user = refreshed.user;
				event.locals.access = refreshed.access;
				event.locals.accessToken = refreshed.accessToken;
				event.locals.accessTokenExpiration = refreshed.accessTokenExpiration;
			}
		}
	}

	// Dev-only: inline the master simulation-clock Date shim into <head> (replacing the app.html
	// placeholder) so Date is patched before app boot. Gated on PUBLIC_SIMULATION_ENABLED via
	// $env/dynamic/public (M2 — dynamic, so an unset var never breaks the build); replaced with '' in
	// production so the served HTML is unchanged.
	const simClockEnabled = env.PUBLIC_SIMULATION_ENABLED === 'true';
	return resolve(event, {
		transformPageChunk: ({ html }) =>
			html.replace(SIM_CLOCK_SHIM_PLACEHOLDER, simClockEnabled ? simClockShimScript : '')
	});
};

/**
 * Refresh the access token from the app-namespaced refresh cookie, using the shared
 * single-flight to avoid racing with the /api/auth/refresh proxy endpoint.
 */
async function tryRefreshToken(
	cookies: Cookies
): Promise<{ user: User; access: AccessEnvelope; accessToken: string; accessTokenExpiration: string } | null> {
	const refreshTokenValue = getRefreshToken(cookies);
	if (!refreshTokenValue) return null;

	const result = await serverRefreshToken(refreshTokenValue);
	if (!result) return null;

	applyRefreshCookies(cookies, result);

	return {
		user: result.user,
		access: result.access,
		accessToken: result.accessToken,
		accessTokenExpiration: result.accessTokenExpiration
	};
}

/**
 * Attach the bearer token to server-side fetches that target the API host.
 * Lets +page.server.ts load functions call `fetch('/api/v1/...')` (or the
 * absolute API URL) and have auth applied automatically.
 */
export const handleFetch: HandleFetch = async ({ event, request, fetch }) => {
	const url = new URL(request.url);
	const accessToken = event.locals.accessToken ?? getAccessToken(event.cookies);

	if (
		accessToken &&
		url.hostname === API_URL.hostname &&
		url.port === API_URL.port &&
		url.protocol === API_URL.protocol
	) {
		request = new Request(request, {
			headers: {
				...Object.fromEntries(request.headers),
				Authorization: `Bearer ${accessToken}`
			}
		});
	}

	return fetch(request);
};
