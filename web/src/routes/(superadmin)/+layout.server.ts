/**
 * Platform super-admin shell guard (IA Wave 1, F6 / TSK-212).
 *
 * Operator-only surfaces (Engine Health, etc.) live behind their own shell, never the
 * landlord sidebar. Access is gated by the PLATFORM_ADMIN_EMAILS allowlist, not a role
 * (Rental Command has no super-admin role). Unauthenticated -> /login; authenticated but
 * not on the allowlist -> 404 (don't even confirm the shell exists to ordinary admins).
 */

import { error, redirect } from '@sveltejs/kit';
import type { LayoutServerLoad } from './$types';
import { isPlatformAdmin } from '$lib/server/platform-admin';

export const load: LayoutServerLoad = async ({ locals, url }) => {
	if (!locals.user) {
		const redirectTo = url.pathname + url.search;
		throw redirect(303, `/login?redirectTo=${encodeURIComponent(redirectTo)}`);
	}

	if (!isPlatformAdmin(locals.user)) {
		throw error(404, 'Not found');
	}

	return {
		user: locals.user,
		accessToken: locals.accessToken,
		accessTokenExpiration: locals.accessTokenExpiration ?? null
	};
};
