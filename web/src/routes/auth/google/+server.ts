/**
 * Initiates the Google OAuth2 flow.
 *
 * Redirects the browser to the Google consent screen. If PUBLIC_GOOGLE_CLIENT_ID
 * is not configured, redirects back to /login with an error notice instead.
 */

import { redirect } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { env } from '$env/dynamic/public';

export const GET: RequestHandler = async ({ url }) => {
	const clientId = env.PUBLIC_GOOGLE_CLIENT_ID ?? '';

	// Guard: if Google Sign-In is not configured, send the user back to login.
	if (!clientId) {
		throw redirect(302, '/login?error=google_unavailable');
	}

	const redirectUri = `${url.origin}/auth/google/callback`;

	const params = new URLSearchParams({
		client_id: clientId,
		redirect_uri: redirectUri,
		response_type: 'code',
		scope: 'openid email profile',
		access_type: 'online',
		prompt: 'select_account'
	});

	throw redirect(302, `https://accounts.google.com/o/oauth2/v2/auth?${params.toString()}`);
};
