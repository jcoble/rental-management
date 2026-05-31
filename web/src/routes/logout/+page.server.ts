/**
 * Logout — supports GET (direct navigation to /logout) and POST (form action).
 * Clears the local session cookies and best-effort revokes the refresh token
 * on the API.
 */

import { redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Cookies } from '@sveltejs/kit';
import { SERVER_API_BASE_URL } from '$lib/server/config';
import { AUTH_COOKIE_NAMES, deleteAuthCookies, getRefreshToken } from '$lib/server/auth-cookies';

async function revokeRefreshToken(refreshToken: string) {
	const controller = new AbortController();
	const timeout = setTimeout(() => controller.abort(), 1500);
	try {
		await fetch(`${SERVER_API_BASE_URL}/auth/logout`, {
			method: 'POST',
			signal: controller.signal,
			headers: { Cookie: `${AUTH_COOKIE_NAMES.refreshToken}=${refreshToken}` }
		});
	} catch {
		// Local cookie deletion is authoritative for this browser session.
	} finally {
		clearTimeout(timeout);
	}
}

function performLogout(cookies: Cookies) {
	const refreshToken = getRefreshToken(cookies);
	deleteAuthCookies(cookies);
	if (refreshToken) void revokeRefreshToken(refreshToken);
}

export const load: PageServerLoad = async ({ cookies }) => {
	performLogout(cookies);
	throw redirect(303, '/login');
};

export const actions: Actions = {
	default: async ({ cookies }) => {
		performLogout(cookies);
		throw redirect(303, '/login');
	}
};
