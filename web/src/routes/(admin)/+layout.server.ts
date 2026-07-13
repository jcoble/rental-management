/**
 * Admin layout guard. Requires authentication AND the Admin role.
 * Unauthenticated -> /login; authenticated-but-not-admin -> 403.
 */

import { error, redirect } from '@sveltejs/kit';
import type { LayoutServerLoad } from './$types';
import { canAccessRoute } from '$lib/auth/experience-policy';

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
		throw error(403, 'Workspace administration access required');
	}

	return {
		user: locals.user,
		accessToken: locals.accessToken,
		accessTokenExpiration: locals.accessTokenExpiration ?? null,
		access: locals.access
	};
};
