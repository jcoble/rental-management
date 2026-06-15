import type { Cookies } from '@sveltejs/kit';

export const AUTH_COOKIE_NAMES = {
	accessToken: 'rc_access_token',
	accessTokenExpiration: 'rc_access_token_expiration',
	refreshToken: 'rc_refresh_token',
	/** Short-lived anti-CSRF state for the Google OAuth2 authorization-code flow. */
	oauthState: 'rc_oauth_state'
} as const;

export const LEGACY_AUTH_COOKIE_NAMES = {
	accessToken: 'access_token',
	accessTokenExpiration: 'access_token_expiration',
	refreshToken: 'refresh_token'
} as const;

const COOKIE_PATH = '/';

export function getAccessToken(cookies: Cookies): string | undefined {
	return cookies.get(AUTH_COOKIE_NAMES.accessToken);
}

export function getAccessTokenExpiration(cookies: Cookies): string | undefined {
	return cookies.get(AUTH_COOKIE_NAMES.accessTokenExpiration);
}

export function getRefreshToken(cookies: Cookies): string | undefined {
	return cookies.get(AUTH_COOKIE_NAMES.refreshToken);
}

export function deleteAccessCookies(cookies: Cookies): void {
	cookies.delete(AUTH_COOKIE_NAMES.accessToken, { path: COOKIE_PATH });
	cookies.delete(AUTH_COOKIE_NAMES.accessTokenExpiration, { path: COOKIE_PATH });
	cookies.delete(LEGACY_AUTH_COOKIE_NAMES.accessToken, { path: COOKIE_PATH });
	cookies.delete(LEGACY_AUTH_COOKIE_NAMES.accessTokenExpiration, { path: COOKIE_PATH });
}

export function deleteAuthCookies(cookies: Cookies): void {
	deleteAccessCookies(cookies);
	cookies.delete(AUTH_COOKIE_NAMES.refreshToken, { path: COOKIE_PATH });
	cookies.delete(LEGACY_AUTH_COOKIE_NAMES.refreshToken, { path: COOKIE_PATH });
}

export function deleteLegacyAuthCookies(cookies: Cookies): void {
	cookies.delete(LEGACY_AUTH_COOKIE_NAMES.accessToken, { path: COOKIE_PATH });
	cookies.delete(LEGACY_AUTH_COOKIE_NAMES.accessTokenExpiration, { path: COOKIE_PATH });
	cookies.delete(LEGACY_AUTH_COOKIE_NAMES.refreshToken, { path: COOKIE_PATH });
}
