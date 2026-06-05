/**
 * Register page server action.
 *
 * Posts to the API's register endpoint. On success it does NOT log the user
 * in — the API sends a verification email instead. The action returns a
 * `registered: true` flag (with the submitted email) so the page can show
 * the "Check your email" confirmation state.
 */

import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { SERVER_API_BASE_URL } from '$lib/server/config';
import { env } from '$env/dynamic/public';

export const load: PageServerLoad = async ({ locals }) => {
	// Already logged in — nothing to expose; layout redirect handles the guard.
	void locals;
	return {
		googleEnabled: Boolean(env.PUBLIC_GOOGLE_CLIENT_ID)
	};
};

export const actions: Actions = {
	default: async ({ request }) => {
		const formData = await request.formData();
		const displayName = formData.get('displayName')?.toString().trim();
		const email = formData.get('email')?.toString().trim();
		const password = formData.get('password')?.toString();
		const confirmPassword = formData.get('confirmPassword')?.toString();

		// Client-side mirrors these but we validate server-side too.
		if (!displayName || !email || !password || !confirmPassword) {
			return fail(400, {
				error: 'All fields are required.',
				displayName,
				email
			});
		}

		if (password !== confirmPassword) {
			return fail(400, {
				error: 'Passwords do not match.',
				displayName,
				email
			});
		}

		if (password.length < 8) {
			return fail(400, {
				error: 'Password must be at least 8 characters.',
				displayName,
				email
			});
		}

		try {
			const response = await fetch(`${SERVER_API_BASE_URL}/auth/register`, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ email, password, displayName })
			});

			if (!response.ok) {
				const errorData = await response.json().catch(() => ({ error: 'Registration failed' }));
				const message =
					typeof errorData.error === 'object'
						? errorData.error?.message
						: errorData.error;
				const details =
					typeof errorData.details === 'string' ? errorData.details : undefined;
				return fail(response.status, {
					error: message || 'Registration failed. Please try again.',
					details,
					displayName,
					email
				});
			}

			// Success: show the "check your email" confirmation state.
			return { registered: true, email };
		} catch (err) {
			console.error('Register error:', err);
			const message = err instanceof Error ? err.message : 'An unexpected error occurred';
			const userMessage =
				message.includes('fetch failed') || message.includes('ECONNREFUSED')
					? 'Unable to connect to the server. Please try again later.'
					: 'An unexpected error occurred. Please try again.';
			return fail(500, { error: userMessage, displayName, email });
		}
	}
};
