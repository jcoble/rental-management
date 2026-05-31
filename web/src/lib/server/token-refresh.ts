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
 *  - a successful result is cached briefly so rapid sequential refreshes
 *    (before the browser has received the rotated cookie) reuse it instead of
 *    replaying an already-consumed token.
 *
 * The API contract (see RentalCommand.Api AuthController):
 *  - POST /api/v1/auth/refresh reads the `refresh_token` cookie, returns a
 *    LoginResponse body, and sets a rotated `refresh_token` cookie
 *    (httpOnly, Secure, SameSite=Strict).
 */

import type { Cookies } from '@sveltejs/kit';
import type { LoginResponse, User } from '$lib/types/user';
import { SERVER_API_BASE_URL } from '$lib/server/config';

const REFRESH_FETCH_TIMEOUT_MS = 10_000;
const CACHE_TTL_MS = 5_000; // enough for the rotated cookie to round-trip to the browser

export interface RefreshResult {
	user: User;
	accessToken: string;
	accessTokenExpiration: string;
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
		cachedResults.set(refreshToken, { result, expiresAt: Date.now() + CACHE_TTL_MS });
	}
	return result;
}

async function doRefresh(refreshToken: string): Promise<RefreshResult | null> {
	const controller = new AbortController();
	const timeout = setTimeout(() => controller.abort(), REFRESH_FETCH_TIMEOUT_MS);
	try {
		const response = await fetch(`${SERVER_API_BASE_URL}/auth/refresh`, {
			method: 'POST',
			headers: {
				'Content-Type': 'application/json',
				Cookie: `refresh_token=${refreshToken}`
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
			const refreshMatch = setCookieHeader.match(/refresh_token=([^;]+)/);
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
		cookies.set('refresh_token', result.newRefreshToken, {
			path: '/',
			httpOnly: true,
			secure: true,
			sameSite: 'lax',
			expires: result.refreshTokenExpires ?? new Date(Date.now() + 7 * 24 * 60 * 60 * 1000)
		});
	}

	cookies.set('access_token', result.accessToken, {
		path: '/',
		httpOnly: true,
		secure: true,
		sameSite: 'lax',
		expires: new Date(result.accessTokenExpiration)
	});

	cookies.set('access_token_expiration', result.accessTokenExpiration, {
		path: '/',
		httpOnly: true,
		secure: true,
		sameSite: 'lax',
		expires: new Date(result.accessTokenExpiration)
	});
}
