import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { SERVER_API_BASE_URL } from '$lib/server/config';

export const load: PageServerLoad = async ({ url }) => {
	const token = url.searchParams.get('token');
	return { invalidLink: !token, token };
};

export const actions: Actions = {
	default: async ({ request }) => {
		const formData = await request.formData();
		const token = formData.get('token')?.toString();
		const password = formData.get('password')?.toString();
		const confirmPassword = formData.get('confirmPassword')?.toString();
		if (!token) return fail(400, { error: 'Invalid activation link.', activated: false });
		if (!password || !confirmPassword) {
			return fail(400, { error: 'Please fill in both password fields.', activated: false });
		}
		if (password !== confirmPassword) {
			return fail(400, { error: 'Passwords do not match.', activated: false });
		}
		if (password.length < 8) {
			return fail(400, { error: 'Password must be at least 8 characters.', activated: false });
		}

		try {
			const response = await fetch(
				`${SERVER_API_BASE_URL}/auth/workspace-invitations/activate`,
				{
					method: 'POST',
					headers: { 'Content-Type': 'application/json' },
					body: JSON.stringify({ token, password })
				}
			);
			if (!response.ok) {
				const problem = await response.json().catch(() => null);
				const message =
					problem?.errors?.Password?.[0] ??
					problem?.error ??
					'This activation link is invalid, expired, or has already been used.';
				return fail(response.status, { error: message, activated: false });
			}
			return { activated: true };
		} catch (error) {
			console.error('Team activation error:', error);
			return fail(500, {
				error: 'Unable to connect to Rental Command. Please try again.',
				activated: false
			});
		}
	}
};
