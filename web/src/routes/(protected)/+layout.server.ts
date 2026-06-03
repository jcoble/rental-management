/**
 * Protected (staff app) layout guard. Requires an authenticated user; sends
 * unauthenticated visitors to /login with a redirectTo back to the page they
 * tried to reach.
 */

import { redirect } from '@sveltejs/kit';
import type { LayoutServerLoad } from './$types';

export const load: LayoutServerLoad = async ({ locals, url }) => {
	if (!locals.user) {
		const redirectTo = url.pathname + url.search;
		throw redirect(303, `/login?redirectTo=${encodeURIComponent(redirectTo)}`);
	}

	if (locals.user.roles?.includes('Tenant') && !locals.user.roles.some((r) => ['Admin', 'Manager', 'Agent'].includes(r))) {
		throw redirect(303, '/portal');
	}

	return {
		user: locals.user,
		accessToken: locals.accessToken,
		accessTokenExpiration: locals.accessTokenExpiration ?? null
	};
};
