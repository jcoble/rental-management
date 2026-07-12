/** Tenant-experience layout guard. Record access remains server-authoritative. */

import { redirect } from '@sveltejs/kit';
import type { LayoutServerLoad } from './$types';

export const load: LayoutServerLoad = async ({ locals, url }) => {
	if (!locals.user) {
		const redirectTo = url.pathname + url.search;
		throw redirect(303, `/login?redirectTo=${encodeURIComponent(redirectTo)}`);
	}

	// Keep this criterion exactly complementary to the protected shell guard.
	const isPortalUser = locals.access?.selectedContext.activeExperience === 'Tenant';
	if (!isPortalUser) {
		throw redirect(303, '/');
	}

	return {
		user: locals.user,
		accessToken: locals.accessToken,
		accessTokenExpiration: locals.accessTokenExpiration ?? null,
		access: locals.access
	};
};
