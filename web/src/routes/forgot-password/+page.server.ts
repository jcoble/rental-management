/**
 * Forgot-password page server action.
 *
 * The API returns 200 regardless of whether an account exists, to prevent account
 * enumeration. Infrastructure failures are different: they must remain retryable
 * and must not be presented as if an email was sent.
 */

import { fail } from '@sveltejs/kit';
import { createHash } from 'node:crypto';
import type { Actions, PageServerLoad } from './$types';
import { serverPost } from '$lib/api/server-fetch';

export const load: PageServerLoad = async ({ url }) => {
	return { email: url.searchParams.get('email')?.trim() ?? '' };
};

export const actions: Actions = {
	default: async ({ request }) => {
		const formData = await request.formData();
		const email = formData.get('email')?.toString().trim();

		if (!email) {
			return fail(400, { error: 'Email is required.', sent: false });
		}

		try {
			// One-minute intent buckets preserve the same key across an ambiguous retry without
			// preventing the user from deliberately requesting another email later.
			const operationKey = createHash('sha256')
				.update(`${email}:${Math.floor(Date.now() / 60_000)}`)
				.digest('hex');
			const result = await serverPost(
				'/auth/forgot-password',
				undefined,
				{ email },
				{ headers: { 'Idempotency-Key': operationKey } }
			);
			if (result.networkError) {
				return fail(503, {
					error: 'Unable to connect to Rental Command. Please try again.',
					sent: false,
					email
				});
			}
			if (result.status < 200 || result.status >= 300) {
				return fail(503, {
					error: 'Password recovery is temporarily unavailable. Please try again.',
					sent: false,
					email
				});
			}
		} catch (err) {
			console.error('Forgot password error:', err);
			return fail(503, {
				error: 'Unable to connect to Rental Command. Please try again.',
				sent: false,
				email
			});
		}

		// Show the neutral success state only after the API request completed successfully.
		return { sent: true };
	}
};
