/**
 * Forgot-password page server action.
 *
 * The API ALWAYS returns 200 regardless of whether an account exists, to
 * prevent account enumeration. We show the same generic success message
 * unconditionally after the action runs.
 */

import { fail } from '@sveltejs/kit';
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
			// We fire this request and ignore any non-network error — the API always
			// returns 200 regardless of whether the account exists.
			await fetch(`${SERVER_API_BASE_URL}/auth/forgot-password`, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
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
