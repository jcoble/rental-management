/**
 * Login page server action.
 *
 * Posts credentials to the API, then stores the access token + expiration and
 * copies the rotated refresh-token cookie from the API's Set-Cookie header into
 * first-party httpOnly cookies on the SvelteKit origin.
 */

import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { LoginResponse } from '$lib/types/user';
import { SERVER_API_BASE_URL } from '$lib/server/config';
import { AUTH_COOKIE_NAMES, deleteLegacyAuthCookies } from '$lib/server/auth-cookies';
import { env } from '$env/dynamic/public';

function safeRedirectPath(path: string | null, fallback: string): string {
	if (!path) return fallback;
	// Same-origin path only: must start with a single '/' followed by a non-slash,
	// non-backslash char. Rejects protocol-relative ('//host') and backslash tricks
	// ('/\\host') that browsers normalize into an external origin.
	if (!/^\/[^/\\]/.test(path)) return fallback;
	// Double-check by parsing against a throwaway origin; anything that resolves
	// off-origin (scheme, host, normalized backslashes, control chars) is rejected.
	try {
		const parsed = new URL(path, 'https://placeholder.invalid');
		if (parsed.origin !== 'https://placeholder.invalid') return fallback;
		return parsed.pathname + parsed.search + parsed.hash;
	} catch {
		return fallback;
	}
}

/** Where to send a user after login when no explicit redirect is requested. */
function defaultLandingFor(roles: string[]): string {
	// Owners/tenants live in the portal. Staff land on the dashboard (IA Wave 1, F8): the
	// "Explore sandbox vs set up real portfolio" fork used to be the forced landing every
	// session — now it's optional (still reachable, and Go Live lives in the sandbox banner).
	if (roles.includes('Owner') || roles.includes('Tenant')) return '/portal';
	return '/';
}

export const load: PageServerLoad = async ({ locals, url }) => {
	if (locals.user) {
		const fallback = defaultLandingFor(locals.user.roles);
		throw redirect(303, safeRedirectPath(url.searchParams.get('redirectTo'), fallback));
	}
	return {
		redirectTo: url.searchParams.get('redirectTo'),
		googleError: url.searchParams.get('error') === 'google_unavailable',
		googleEnabled: Boolean(env.PUBLIC_GOOGLE_CLIENT_ID)
	};
};

export const actions: Actions = {
	default: async ({ request, cookies }) => {
		const formData = await request.formData();
		const email = formData.get('email')?.toString();
		const password = formData.get('password')?.toString();
		const requestedRedirect = formData.get('redirectTo')?.toString() ?? null;

		if (!email || !password) {
			return fail(400, { error: 'Email and password are required', email });
		}

		let data: LoginResponse;
		try {
			const response = await fetch(`${SERVER_API_BASE_URL}/auth/login`, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ email, password })
			});

			if (!response.ok) {
				const errorData = await response.json().catch(() => ({ error: 'Login failed' }));
				const errorMessage =
					typeof errorData.error === 'object'
						? errorData.error?.message
						: errorData.error;
				return fail(response.status, {
					error: errorMessage || 'Invalid email or password',
					email
				});
			}

			data = (await response.json()) as LoginResponse;

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
			console.error('Login error:', err);
			const message = err instanceof Error ? err.message : 'An unexpected error occurred';
			const userMessage =
				message.includes('fetch failed') || message.includes('ECONNREFUSED')
					? 'Unable to connect to the API server. Please ensure the backend is running.'
					: 'An unexpected error occurred. Please try again.';
			return fail(500, { error: userMessage, email });
		}

		const fallback = defaultLandingFor(data.user.roles);
		throw redirect(303, safeRedirectPath(requestedRedirect, fallback));
	}
};
