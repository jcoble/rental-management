/**
 * Reset-password page.
 *
 * `load` reads userId + token from the query string and passes them to the
 * page as data so they can be embedded as hidden fields in the form.
 * The server action validates the passwords match, then POSTs to the API.
 */

import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { SERVER_API_BASE_URL } from '$lib/server/config';

export const load: PageServerLoad = async ({ url }) => {
	const userId = url.searchParams.get('userId');
	const token = url.searchParams.get('token');

	if (!userId || !token) {
		return { invalidLink: true, userId: null, token: null };
	}

	return { invalidLink: false, userId, token };
};

export const actions: Actions = {
	default: async ({ request }) => {
		const formData = await request.formData();
		const userId = formData.get('userId')?.toString();
		const token = formData.get('token')?.toString();
		const newPassword = formData.get('newPassword')?.toString();
		const confirmPassword = formData.get('confirmPassword')?.toString();

		if (!userId || !token) {
			return fail(400, { error: 'Invalid reset link.', reset: false });
		}

		if (!newPassword || !confirmPassword) {
			return fail(400, { error: 'Please fill in all fields.', reset: false });
		}

		if (newPassword !== confirmPassword) {
			return fail(400, { error: 'Passwords do not match.', reset: false });
		}

		if (newPassword.length < 8) {
			return fail(400, { error: 'Password must be at least 8 characters.', reset: false });
		}

		try {
			const response = await fetch(`${SERVER_API_BASE_URL}/auth/reset-password`, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ userId, token, newPassword })
			});

			if (!response.ok) {
				const errorData = await response.json().catch(() => ({ error: 'Password reset failed' }));
				const message =
					typeof errorData.error === 'object'
						? errorData.error?.message
						: errorData.error;
				return fail(response.status, {
					error: message || 'Password reset failed. The link may have expired.',
					reset: false
				});
			}

			return { reset: true };
		} catch (err) {
			console.error('Reset password error:', err);
			return fail(500, {
				error: 'Unable to connect to the server. Please try again later.',
				reset: false
			});
		}
	}
};
