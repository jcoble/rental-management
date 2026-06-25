/**
 * Account security server actions shared by staff and tenant portal routes.
 *
 * The change-password endpoint is Bearer-authenticated. We post it from a server action via
 * `serverPost(..., locals.accessToken!)`, which sets the Authorization header exactly once on a
 * direct fetch to the API. We deliberately do NOT use `event.fetch` here: that would route through
 * `handleFetch`, which injects Authorization too, and the double-inject produces a malformed
 * `Bearer X, Bearer Y` header and a silent 401.
 */

import { fail, type RequestEvent } from '@sveltejs/kit';
import { serverPost } from '$lib/api/server-fetch';

export async function changePassword({ request, locals }: RequestEvent) {
	if (!locals.accessToken) {
		return fail(401, { error: 'Your session has expired. Please sign in again.', changed: false });
	}

	const formData = await request.formData();
	const currentPassword = formData.get('currentPassword')?.toString();
	const newPassword = formData.get('newPassword')?.toString();
	const confirmPassword = formData.get('confirmPassword')?.toString();

	if (!currentPassword || !newPassword || !confirmPassword) {
		return fail(400, { error: 'Please fill in all fields.', changed: false });
	}

	if (newPassword !== confirmPassword) {
		return fail(400, { error: 'New passwords do not match.', changed: false });
	}

	if (newPassword.length < 8) {
		return fail(400, { error: 'New password must be at least 8 characters.', changed: false });
	}

	if (newPassword === currentPassword) {
		return fail(400, { error: 'Your new password must be different from your current one.', changed: false });
	}

	const result = await serverPost<{ message: string }>('/auth/change-password', locals.accessToken, {
		currentPassword,
		newPassword
	});

	if (result.error) {
		return fail(result.status || 400, { error: result.error, changed: false });
	}

	return { changed: true };
}
