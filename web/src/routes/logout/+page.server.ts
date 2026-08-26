/**
 * Logout — supports GET (direct navigation to /logout) and POST (form action).
 * Clears the local session cookies and revokes the server-side session on the API.
 */

import { redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Cookies } from '@sveltejs/kit';
import { serverPost } from '$lib/api/server-fetch';
import { serverRefreshToken } from '$lib/server/token-refresh';
import {
	AUTH_COOKIE_NAMES,
	deleteAuthCookies,
	getAccessToken,
	getAccessTokenExpiration,
	getRefreshToken
} from '$lib/server/auth-cookies';

const LOGOUT_FETCH_TIMEOUT_MS = 1500;

type RevokeResult = { ok: boolean; status: number };

async function revokeSession(accessToken: string, refreshToken?: string): Promise<RevokeResult> {
	const controller = new AbortController();
	const timeout = setTimeout(() => controller.abort(), LOGOUT_FETCH_TIMEOUT_MS);
	try {
		const result = await serverPost('/auth/logout', accessToken, undefined, {
			signal: controller.signal,
			headers: {
				...(refreshToken ? { Cookie: `${AUTH_COOKIE_NAMES.refreshToken}=${refreshToken}` } : {})
			}
		});
		const ok = result.status >= 200 && result.status < 300;
		if (!ok) {
			console.error(`Logout API returned ${result.status}; server session revocation did not succeed.`);
		}
		return { ok, status: result.status };
	} catch (error) {
		console.error('Logout API request failed; server session revocation did not succeed.', error);
		return { ok: false, status: 0 };
	} finally {
		clearTimeout(timeout);
	}
}

function isExpired(expiration: string | undefined): boolean {
	if (!expiration) return false;
	const timestamp = Date.parse(expiration);
	return Number.isFinite(timestamp) && timestamp <= Date.now();
}

async function performLogout(cookies: Cookies): Promise<void> {
	const accessToken = getAccessToken(cookies);
	const refreshToken = getRefreshToken(cookies);
	try {
		let bearer = accessToken;
		if ((!bearer || isExpired(getAccessTokenExpiration(cookies))) && refreshToken) {
			const refreshed = await serverRefreshToken(refreshToken);
			bearer = refreshed?.accessToken;
			if (!bearer) {
				console.error('Logout could not refresh an access token; server session may remain active.');
			}
		}

		if (bearer) {
			const result = await revokeSession(bearer, refreshToken);
			if (!result.ok && result.status === 401 && refreshToken && bearer === accessToken) {
				// The access cookie may be stale even when its companion expiration cookie is missing.
				// Reuse the shared refresh single-flight before giving up on server-side revocation.
				const refreshed = await serverRefreshToken(refreshToken);
				if (refreshed?.accessToken) {
					await revokeSession(refreshed.accessToken, refreshToken);
				}
			}
		}
	} catch (error) {
		// Sign-out must remain usable if the API or refresh path is unavailable.
		console.error('Logout could not revoke the server session.', error);
	} finally {
		deleteAuthCookies(cookies);
	}
}

export const load: PageServerLoad = async ({ cookies }) => {
	await performLogout(cookies);
	throw redirect(303, '/login');
};

export const actions: Actions = {
	default: async ({ cookies }) => {
		await performLogout(cookies);
		throw redirect(303, '/login');
	}
};
