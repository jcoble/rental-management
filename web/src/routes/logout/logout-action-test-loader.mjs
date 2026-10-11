export async function resolve(specifier, context, nextResolve) {
	if (specifier === '$lib/api/server-fetch') {
		return {
			url: new URL('../../lib/api/server-fetch.ts', import.meta.url).href,
			shortCircuit: true
		};
	}

	if (specifier === '$lib/server/config') {
		return {
			url: 'data:text/javascript,export%20const%20SERVER_API_BASE_URL%20%3D%20%22https%3A%2F%2Fapi.test%2Fapi%2Fv1%22%3B',
			shortCircuit: true
		};
	}

	if (specifier === '$lib/server/auth-cookies') {
		const source = `
			export const AUTH_COOKIE_NAMES = {
				accessToken: 'rc_access_token',
				accessTokenExpiration: 'rc_access_token_expiration',
				refreshToken: 'rc_refresh_token'
			};
			export function getAccessToken(cookies) { return cookies.get(AUTH_COOKIE_NAMES.accessToken); }
			export function getAccessTokenExpiration(cookies) { return cookies.get(AUTH_COOKIE_NAMES.accessTokenExpiration); }
			export function getRefreshToken(cookies) { return cookies.get(AUTH_COOKIE_NAMES.refreshToken); }
			export function deleteAuthCookies(cookies) {
				cookies.delete(AUTH_COOKIE_NAMES.accessToken, { path: '/' });
				cookies.delete(AUTH_COOKIE_NAMES.accessTokenExpiration, { path: '/' });
				cookies.delete(AUTH_COOKIE_NAMES.refreshToken, { path: '/' });
			}
		`;
		return { url: `data:text/javascript,${encodeURIComponent(source)}`, shortCircuit: true };
	}

	if (specifier === '$lib/server/token-refresh') {
		const source = `
			export async function serverRefreshToken(refreshToken) {
				globalThis.__logoutRefreshCalls = (globalThis.__logoutRefreshCalls ?? 0) + 1;
				return refreshToken ? { accessToken: 'refreshed-access-token' } : null;
			}
		`;
		return { url: `data:text/javascript,${encodeURIComponent(source)}`, shortCircuit: true };
	}

	return nextResolve(specifier, context);
}
