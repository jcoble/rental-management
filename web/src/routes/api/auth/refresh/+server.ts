/**
 * Same-origin proxy for token refresh.
 *
 * The refresh-token cookie is httpOnly and first-party to the SvelteKit origin,
 * so client JS cannot send it cross-origin to the API. This endpoint reads the
 * cookie, refreshes via the shared single-flight (avoiding races with
 * hooks.server.ts), sets the rotated cookies, and returns the new access token.
 */

import { json, error } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { serverRefreshToken, applyRefreshCookies } from '$lib/server/token-refresh';
import { deleteAuthCookies, getRefreshToken } from '$lib/server/auth-cookies';

export const POST: RequestHandler = async ({ cookies }) => {
	const refreshTokenValue = getRefreshToken(cookies);
	if (!refreshTokenValue) {
		throw error(401, 'No refresh token');
	}

	const result = await serverRefreshToken(refreshTokenValue);
	if (!result) {
		deleteAuthCookies(cookies);
		throw error(401, 'Token refresh failed');
	}

	applyRefreshCookies(cookies, result);

	return json({
		accessToken: result.accessToken,
		accessTokenExpiration: result.accessTokenExpiration,
		access: result.access
	});
};
