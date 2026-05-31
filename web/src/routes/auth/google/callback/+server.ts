/**
 * Google OAuth2 callback handler.
 *
 * Receives the authorization code from Google, exchanges it with the API for
 * tokens, sets auth cookies (mirroring the login server action exactly), then
 * redirects to the app home. On any error, redirects to /login?error=google_unavailable.
 */

import { redirect } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import type { LoginResponse } from '$lib/types/user';
import { SERVER_API_BASE_URL } from '$lib/server/config';
import { AUTH_COOKIE_NAMES, deleteLegacyAuthCookies } from '$lib/server/auth-cookies';

export const GET: RequestHandler = async ({ url, cookies }) => {
	// Google may return an error (e.g. user denied consent).
	const oauthError = url.searchParams.get('error');
	if (oauthError) {
		throw redirect(302, '/login?error=google_unavailable');
	}

	const code = url.searchParams.get('code');
	if (!code) {
		throw redirect(302, '/login?error=google_unavailable');
	}

	const redirectUri = `${url.origin}/auth/google/callback`;

	let data: LoginResponse;
	try {
		const response = await fetch(`${SERVER_API_BASE_URL}/auth/google`, {
			method: 'POST',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify({ code, redirectUri })
		});

		if (!response.ok) {
			// 501 = Google not configured server-side; any other error is also fatal here.
			throw redirect(302, '/login?error=google_unavailable');
		}

		data = (await response.json()) as LoginResponse;

		// ---- Mirror the login server action cookie-setting logic exactly ----

		// Access token cookie (used for SSR Authorization headers).
		cookies.set(AUTH_COOKIE_NAMES.accessToken, data.accessToken, {
			path: '/',
			httpOnly: true,
			secure: true,
			sameSite: 'lax',
			expires: new Date(data.accessTokenExpiration)
		});
		cookies.set(AUTH_COOKIE_NAMES.accessTokenExpiration, data.accessTokenExpiration, {
			path: '/',
			httpOnly: true,
			secure: true,
			sameSite: 'lax',
			expires: new Date(data.accessTokenExpiration)
		});

		// Copy the refresh token from the API's Set-Cookie into a first-party
		// cookie on this origin so client JS can refresh via our proxy.
		const setCookieHeader = response.headers.get('set-cookie');
		if (setCookieHeader) {
			const refreshMatch = setCookieHeader.match(
				new RegExp(`${AUTH_COOKIE_NAMES.refreshToken}=([^;]+)`)
			);
			if (refreshMatch) {
				const expiresMatch = setCookieHeader.match(/expires=([^;]+)/i);
				const expires = expiresMatch
					? new Date(expiresMatch[1])
					: new Date(Date.now() + 7 * 24 * 60 * 60 * 1000);
				cookies.set(AUTH_COOKIE_NAMES.refreshToken, refreshMatch[1], {
					path: '/',
					httpOnly: true,
					secure: true,
					sameSite: 'lax',
					expires
				});
			}
		}
		deleteLegacyAuthCookies(cookies);
	} catch (err) {
		// Re-throw SvelteKit redirects (thrown as Response-like objects internally).
		if (err instanceof Response) {
			throw err;
		}
		console.error('Google OAuth callback error:', err);
		throw redirect(302, '/login?error=google_unavailable');
	}

	throw redirect(303, '/');
};
