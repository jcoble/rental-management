/**
 * Login page server action.
 *
 * Posts credentials to the API, then stores the access token + expiration and
 * copies the rotated refresh-token cookie from the API's Set-Cookie header into
 * first-party httpOnly cookies on the SvelteKit origin.
 */

import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { AccessContextSelectionRequiredResponse, LoginResponse } from '$lib/types/user';
import { serverPost } from '$lib/api/server-fetch';
import { AUTH_COOKIE_NAMES, deleteLegacyAuthCookies } from '$lib/server/auth-cookies';
import { env } from '$env/dynamic/public';
import { canAccessPathForEnvelope, safeLandingForAccess } from '$lib/auth/experience-policy';

function authorizedRedirectPath(
	path: string | null,
	fallback: string,
	access: LoginResponse['access']
): string {
	const candidate = safeRedirectPath(path, fallback);
	const pathname = new URL(candidate, 'https://placeholder.invalid').pathname;
	return canAccessPathForEnvelope(access, pathname) ? candidate : fallback;
}

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

export const load: PageServerLoad = async ({ locals, url }) => {
	if (locals.user) {
		const fallback = locals.access ? (safeLandingForAccess(locals.access) ?? '/logout') : '/logout';
		throw redirect(303, authorizedRedirectPath(url.searchParams.get('redirectTo'), fallback, locals.access!));
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
		const accessContextValue = formData.get('accessContextId')?.toString();
		const accessContextId = accessContextValue ? Number(accessContextValue) : undefined;

		if (!email || !password) {
			return fail(400, { error: 'Email and password are required', emailNotVerified: false, email });
		}

		let data: LoginResponse;
		try {
			const result = await serverPost<LoginResponse>(
				'/auth/login',
				undefined,
				{ email, password, accessContextId }
			);

			if (result.status < 200 || result.status >= 300 || !result.data) {
				const errorData = result.problem ?? {};
				if (result.status === 409 && errorData.code === 'ACCESS_CONTEXT_REQUIRED') {
					const selection = errorData as unknown as AccessContextSelectionRequiredResponse;
					return fail(409, {
						error: selection.error,
						emailNotVerified: false,
						email,
						contexts: selection.contexts
					});
				}
				const rawError = errorData.error;
				const errorMessage =
					typeof rawError === 'string'
						? rawError
						: (rawError as { message?: string } | undefined)?.message ?? result.error;
				// The API marks an unverified-email login with an EMAIL_NOT_VERIFIED: prefix. Surface a
				// clean boolean (and keep the email) so the page can offer a "resend verification" action
				// instead of just printing the raw marker string.
				const emailNotVerified =
					typeof errorMessage === 'string' && errorMessage.includes('EMAIL_NOT_VERIFIED');
				if (result.networkError) {
					const message = result.error ?? '';
					const userMessage =
						message.includes('fetch failed') || message.includes('ECONNREFUSED')
							? 'Unable to connect to the API server. Please ensure the backend is running.'
							: 'An unexpected error occurred. Please try again.';
					return fail(500, { error: userMessage, emailNotVerified: false, email });
				}
				return fail(result.status, {
					error: emailNotVerified
						? 'Please verify your email address before signing in.'
						: errorMessage || 'Invalid email or password',
					emailNotVerified,
					email
				});
			}

			data = result.data;

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
			const setCookieHeader = result.responseHeaders?.get('set-cookie');
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
			return fail(500, { error: userMessage, emailNotVerified: false, email });
		}

		const fallback = safeLandingForAccess(data.access) ?? '/logout';
		throw redirect(303, authorizedRedirectPath(requestedRedirect, fallback, data.access));
	}
};
