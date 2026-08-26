/**
 * Reset-password page.
 *
 * `load` reads userId + token from the query string and passes them to the
 * page as data so they can be embedded as hidden fields in the form.
 * The server action validates the passwords match, then POSTs to the API.
 */

import { fail } from '@sveltejs/kit';
import { createHash } from 'node:crypto';
import type { Actions, PageServerLoad } from './$types';
import { serverPost } from '$lib/api/server-fetch';

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
			const operationKey = createHash('sha256')
				.update(JSON.stringify({ userId, token }))
				.digest('hex');
			const result = await serverPost(
				'/auth/reset-password',
				undefined,
				{ userId, token, newPassword },
				{ headers: { 'Idempotency-Key': operationKey } }
			);

			if (result.networkError) {
				return fail(500, {
					error: 'Unable to connect to the server. Please try again later.',
					reset: false
				});
			}
			if (result.status < 200 || result.status >= 300) {
				const rawError = result.problem?.error;
				const message =
					typeof rawError === 'string'
						? rawError
						: (rawError as { message?: string } | undefined)?.message;
				return fail(result.status, {
					error: message || 'Password reset failed',
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
