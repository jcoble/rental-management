/**
 * Forgot-password page server action.
 *
 * The API ALWAYS returns 200 regardless of whether an account exists, to
 * prevent account enumeration. We show the same generic success message
 * unconditionally after the action runs.
 */

import { fail } from '@sveltejs/kit';
import { createHash } from 'node:crypto';
import type { Actions, PageServerLoad } from './$types';
import { SERVER_API_BASE_URL } from '$lib/server/config';

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
			// We fire this request and ignore any non-network error — the API always
			// returns 200 regardless of whether the account exists.
			await fetch(`${SERVER_API_BASE_URL}/auth/forgot-password`, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json', 'Idempotency-Key': operationKey },
				body: JSON.stringify({ email })
			});
		} catch (err) {
			console.error('Forgot password error:', err);
			// Even on network error we show the generic message to avoid leaking info.
		}

		// Always show the generic success state.
		return { sent: true };
	}
};
