/**
 * Verify-email page.
 *
 * The API sends links of the form /verify-email?userId={id}&token={urlEncodedToken}.
 * We perform the confirmation in `load` (the email link is a plain GET navigation)
 * so there's nothing to submit — the user just opens the link and sees the result.
 */

import type { PageServerLoad } from './$types';
import { createHash } from 'node:crypto';
import { serverPost } from '$lib/api/server-fetch';

export const load: PageServerLoad = async ({ url }) => {
	const userId = url.searchParams.get('userId');
	const token = url.searchParams.get('token');

	if (!userId || !token) {
		return { success: false, error: 'Invalid verification link.' };
	}

	try {
		const operationKey = createHash('sha256')
			.update(JSON.stringify({ userId, token }))
			.digest('hex');
		const result = await serverPost<{ message?: string }>(
			'/auth/confirm-email',
			undefined,
			{ userId, token },
			{ headers: { 'Idempotency-Key': operationKey } }
		);

		if (result.networkError) {
			return {
				success: false,
				error: 'Unable to connect to the server. Please try again later.'
			};
		}
		if (result.status < 200 || result.status >= 300) {
			const rawError = result.problem?.error;
			const message =
				typeof rawError === 'string'
					? rawError
					: (rawError as { message?: string } | undefined)?.message;
			return { success: false, error: message || 'Verification failed.' };
		}

		return { success: true, message: result.data?.message ?? 'Email verified.' };
	} catch (err) {
		console.error('Email verification error:', err);
		return {
			success: false,
			error: 'Unable to connect to the server. Please try again later.'
		};
	}
};
