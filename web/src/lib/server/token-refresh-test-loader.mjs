export async function resolve(specifier, context, nextResolve) {
	if (specifier === '$lib/server/config') {
		return {
			url: 'data:text/javascript,export%20const%20SERVER_API_BASE_URL%20%3D%20%22https%3A%2F%2Fapi.test%2Fapi%2Fv1%22%3B',
			shortCircuit: true
		};
	}

	if (specifier === '$lib/server/auth-cookies') {
		return {
			url: 'data:text/javascript,export%20const%20AUTH_COOKIE_NAMES%20%3D%20%7BrefreshToken%3A%20%22rc_refresh_token%22%2C%20accessToken%3A%20%22rc_access_token%22%2C%20accessTokenExpiration%3A%20%22rc_access_token_expiration%22%7D%3B',
			shortCircuit: true
		};
	}

	return nextResolve(specifier, context);
}
