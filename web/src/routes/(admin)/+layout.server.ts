/**
 * Admin layout guard. Requires authentication AND the Admin role.
 * Unauthenticated -> /login; authenticated-but-not-admin -> 403.
 */

import { error, redirect } from '@sveltejs/kit';
import type { LayoutServerLoad } from './$types';

export const load: LayoutServerLoad = async ({ locals, url }) => {
	if (!locals.user) {
		const redirectTo = url.pathname + url.search;
		throw redirect(303, `/login?redirectTo=${encodeURIComponent(redirectTo)}`);
	}

	const capabilities = new Set(locals.access?.navigation.flatMap((item) => item.capabilityKeys) ?? []);
	if (!capabilities.has('team.manage') && !capabilities.has('security.manage')) {
		throw error(403, 'Workspace administration access required');
	}

	return {
		user: locals.user,
		accessToken: locals.accessToken,
		accessTokenExpiration: locals.accessTokenExpiration ?? null,
		access: locals.access
	};
};
