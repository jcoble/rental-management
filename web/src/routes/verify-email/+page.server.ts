/**
 * Verify-email page.
 *
 * The API sends links of the form /verify-email?userId={id}&token={urlEncodedToken}.
 * We perform the confirmation in `load` (the email link is a plain GET navigation)
 * so there's nothing to submit — the user just opens the link and sees the result.
 */

import type { PageServerLoad } from './$types';
import { createHash } from 'node:crypto';
import { SERVER_API_BASE_URL } from '$lib/server/config';

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
		const response = await fetch(`${SERVER_API_BASE_URL}/auth/confirm-email`, {
			method: 'POST',
			headers: { 'Content-Type': 'application/json', 'Idempotency-Key': operationKey },
			body: JSON.stringify({ userId, token })
		});

		if (!response.ok) {
			const errorData = await response.json().catch(() => ({ error: 'Verification failed' }));
			const message =
				typeof errorData.error === 'object'
					? errorData.error?.message
					: errorData.error;
			return { success: false, error: message || 'Verification failed.' };
		}

		const data = await response.json().catch(() => ({ message: 'Email verified.' }));
		return { success: true, message: data.message as string | undefined };
	} catch (err) {
		console.error('Email verification error:', err);
		return {
			success: false,
			error: 'Unable to connect to the server. Please try again later.'
		};
	}
};
