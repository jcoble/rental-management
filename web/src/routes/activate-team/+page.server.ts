import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { serverPost } from '$lib/api/server-fetch';

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
			const result = await serverPost(
				'/auth/workspace-invitations/activate',
				undefined,
				{ token, password }
			);
			if (result.networkError) {
				return fail(500, {
					error: 'Unable to connect to Rental Command. Please try again.',
					activated: false
				});
			}
			if (result.status < 200 || result.status >= 300) {
				const problem = result.problem;
				const message =
					result.validationErrors?.Password?.[0] ??
					(typeof problem?.error === 'string' ? problem.error : undefined) ??
					'This activation link is invalid, expired, or has already been used.';
				return fail(result.status, { error: message, activated: false });
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
