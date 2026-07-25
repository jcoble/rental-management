/**
 * Admin layout guard. Requires authentication AND the Admin role.
 * Unauthenticated visitors go to login. Authenticated users without access return to the first
 * route their selected experience can open.
 */

import { redirect } from '@sveltejs/kit';
import type { LayoutServerLoad } from './$types';
import { canAccessRoute, safeLandingForAccess } from '$lib/auth/experience-policy';

export const load: LayoutServerLoad = async ({ locals, url }) => {
	if (!locals.user) {
		const redirectTo = url.pathname + url.search;
		throw redirect(303, `/login?redirectTo=${encodeURIComponent(redirectTo)}`);
	}

	const activeExperience = locals.access?.selectedContext.activeExperience;
	const capabilities = new Set(
		locals.access?.navigation.find((item) => item.experience === activeExperience)?.capabilityKeys ?? []
	);
	if (!activeExperience || !canAccessRoute(url.pathname, activeExperience, capabilities)) {
		throw redirect(303, locals.access ? (safeLandingForAccess(locals.access) ?? '/logout') : '/logout');
	}

	return {
		user: locals.user,
		accessToken: locals.accessToken,
		accessTokenExpiration: locals.accessTokenExpiration ?? null,
		access: locals.access
	};
};
