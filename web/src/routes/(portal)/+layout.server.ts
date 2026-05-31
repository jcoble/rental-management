/**
 * Portal layout guard. Requires authentication. Owners and tenants are the
 * primary audience; staff (Admin/Manager/Agent) may also view the portal
 * (e.g. to assist a resident), so any authenticated user is allowed through.
 */

import { redirect } from '@sveltejs/kit';
import type { LayoutServerLoad } from './$types';

export const load: LayoutServerLoad = async ({ locals, url }) => {
	if (!locals.user) {
		const redirectTo = url.pathname + url.search;
		throw redirect(303, `/login?redirectTo=${encodeURIComponent(redirectTo)}`);
	}

	return {
		user: locals.user,
		accessToken: locals.accessToken,
		accessTokenExpiration: locals.accessTokenExpiration ?? null
	};
};
