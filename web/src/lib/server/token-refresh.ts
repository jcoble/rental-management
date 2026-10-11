/**
 * Server-side token refresh with single-flight deduplication + brief cache.
 *
 * Both hooks.server.ts and the /api/auth/refresh proxy endpoint can trigger a
 * token refresh concurrently (e.g. after laptop sleep/wake, or SignalR +
 * polling both invalidating). The RentalCommand API rotates refresh tokens
 * (single-use), so two concurrent refreshes with the same token would cause
 * the second to fail and log the user out.
 *
 * This module guarantees:
 *  - only ONE refresh request per refresh-token value is in flight at a time
 *    (concurrent callers share the same result), and
 *  - a successful result is cached (keyed by the OLD token value that was
 *    exchanged) for long enough to outlast cookie propagation so that any
 *    straggler still holding the old cookie reuses the already-rotated result
 *    instead of replaying an already-consumed token to the API.
 *
 * Why the cache window matters: an access token expires after ~15 min, at which
 * point SSR (hooks.server.ts /auth/me 401), the client 401-retry, the client's
 * proactive 120s pre-expiry check, and SignalR's accessTokenFactory can each
 * independently read the SAME refresh cookie and trigger a refresh. They do NOT
 * all fire in the same tick, so the in-flight map alone can't dedupe them — the
 * second one that runs after the first completes would otherwise POST the old
 * (now rotated/single-use) token to the API and trip reuse-detection, revoking
 * the whole token family and logging the user out. Caching the rotated result
 * by the old token value collapses all of these into ONE real API rotation.
 *
 * The API contract (see RentalCommand.Api AuthController):
 *  - POST /api/v1/auth/refresh reads the app-namespaced refresh cookie,
 *    returns a LoginResponse body, and sets a rotated refresh cookie
 *    (httpOnly, Secure, SameSite=Strict).
 */

import type { Cookies } from '@sveltejs/kit';
import type { AccessEnvelope, LoginResponse, User } from '$lib/types/user';
import { SERVER_API_BASE_URL } from '$lib/server/config';
import { AUTH_COOKIE_NAMES } from '$lib/server/auth-cookies';

const REFRESH_FETCH_TIMEOUT_MS = 10_000;
// How long a rotated result is re-served for the OLD token value that produced
// it. Must comfortably outlast the worst-case gap between independent refresh
// triggers reading the same not-yet-replaced cookie (SSR /auth/me 401, client
// 401-retry, proactive 120s pre-expiry check, SignalR accessTokenFactory) plus
// cookie round-trip. 30s is well within the access token's ~15-min lifetime, so
// a single rotation per expiry cycle is reused everywhere; a genuinely new login
// or a later expiry presents a different token and refreshes normally.
const CACHE_TTL_MS = 30_000;
// Cap the cache so a long-lived process can't accumulate stale rotated tokens.
const MAX_CACHED_RESULTS = 512;

export interface RefreshResult {
	user: User;
	accessToken: string;
	accessTokenExpiration: string;
	access: AccessEnvelope;
	newRefreshToken?: string;
	refreshTokenExpires?: Date;
}

const inFlightRefreshes = new Map<string, Promise<RefreshResult | null>>();
const cachedResults = new Map<string, { result: RefreshResult; expiresAt: number }>();

/**
 * Perform a token refresh, deduplicating concurrent calls keyed by the
 * refresh-token value. Returns the result, or null if the refresh failed.
 *
 * IMPORTANT: the caller must apply the resulting cookies (cookies are
 * per-request), e.g. via {@link applyRefreshCookies}.
 */
export async function serverRefreshToken(refreshToken: string): Promise<RefreshResult | null> {
	// Reuse a recent successful result for this exact token. Different browsers
	// for the same user hold different refresh tokens and must not cross-share.
	const cached = cachedResults.get(refreshToken);
	if (cached && Date.now() < cached.expiresAt) {
		return cached.result;
	}
	if (cached) cachedResults.delete(refreshToken);

	const inFlight = inFlightRefreshes.get(refreshToken);
	if (inFlight) {
		return inFlight;
	}

	const refreshPromise = doRefresh(refreshToken).finally(() => {
		inFlightRefreshes.delete(refreshToken);
	});
	inFlightRefreshes.set(refreshToken, refreshPromise);

	const result = await refreshPromise;
	if (result) {
		rememberResult(refreshToken, result);
	}
	return result;
}

/**
 * Cache a rotated result keyed by the OLD token value that produced it, evicting
 * the oldest entry if the cache is full. Bounded so a long-lived node process
 * can't accumulate rotated tokens without limit.
 */
function rememberResult(oldToken: string, result: RefreshResult): void {
	if (cachedResults.size >= MAX_CACHED_RESULTS) {
		// Drop expired entries first; if still full, evict the oldest insertion.
		const now = Date.now();
		for (const [key, entry] of cachedResults) {
			if (now >= entry.expiresAt) cachedResults.delete(key);
		}
		if (cachedResults.size >= MAX_CACHED_RESULTS) {
			const oldestKey = cachedResults.keys().next().value;
			if (oldestKey !== undefined) cachedResults.delete(oldestKey);
		}
	}
	cachedResults.set(oldToken, { result, expiresAt: Date.now() + CACHE_TTL_MS });
}

async function doRefresh(refreshToken: string): Promise<RefreshResult | null> {
	const controller = new AbortController();
	const timeout = setTimeout(() => controller.abort(), REFRESH_FETCH_TIMEOUT_MS);
	try {
		const response = await fetch(`${SERVER_API_BASE_URL}/auth/refresh`, {
			method: 'POST',
			headers: {
				'Content-Type': 'application/json',
				Cookie: `${AUTH_COOKIE_NAMES.refreshToken}=${refreshToken}`
			},
			signal: controller.signal
		});

		if (!response.ok) {
			return null;
		}

		const data: LoginResponse = await response.json();

		// Extract the rotated refresh token from the API's Set-Cookie header.
		let newRefreshToken: string | undefined;
		let refreshTokenExpires: Date | undefined;

		const setCookieHeader = response.headers.get('set-cookie');
		if (setCookieHeader) {
			const refreshMatch = setCookieHeader.match(
				new RegExp(`${AUTH_COOKIE_NAMES.refreshToken}=([^;]+)`)
			);
			if (refreshMatch) {
				newRefreshToken = refreshMatch[1];
				const expiresMatch = setCookieHeader.match(/expires=([^;]+)/i);
				refreshTokenExpires = expiresMatch
					? new Date(expiresMatch[1])
					: new Date(Date.now() + 7 * 24 * 60 * 60 * 1000);
			}
		}

		return {
			user: data.user,
			accessToken: data.accessToken,
			accessTokenExpiration: data.accessTokenExpiration,
			access: data.access,
			newRefreshToken,
			refreshTokenExpires
		};
	} catch (error) {
		console.error('Server token refresh error:', error);
		return null;
	} finally {
		clearTimeout(timeout);
	}
}

/**
 * Apply a refresh result's cookies to a per-request Cookies object.
 * Stores the access token (for SSR Authorization headers), its expiration,
 * and the rotated refresh token.
 */
export function applyRefreshCookies(cookies: Cookies, result: RefreshResult): void {
	if (result.newRefreshToken) {
		cookies.set(AUTH_COOKIE_NAMES.refreshToken, result.newRefreshToken, {
			path: '/',
			httpOnly: true,
			secure: true,
			sameSite: 'lax',
			expires: result.refreshTokenExpires ?? new Date(Date.now() + 7 * 24 * 60 * 60 * 1000)
		});
	}

	cookies.set(AUTH_COOKIE_NAMES.accessToken, result.accessToken, {
		path: '/',
		httpOnly: true,
		secure: true,
		sameSite: 'lax',
		expires: new Date(result.accessTokenExpiration)
	});

	cookies.set(AUTH_COOKIE_NAMES.accessTokenExpiration, result.accessTokenExpiration, {
		path: '/',
		httpOnly: true,
		secure: true,
		sameSite: 'lax',
		expires: new Date(result.accessTokenExpiration)
	});
}
