/**
 * Root layout server load — seeds auth state for every route.
 *
 * locals.user / locals.accessToken are populated by hooks.server.ts. We pass
 * them to the client so the runes auth store can hydrate without a round-trip.
 */

import type { LayoutServerLoad } from './$types';

export const load: LayoutServerLoad = async ({ locals }) => {
	return {
		user: locals.user,
		accessToken: locals.accessToken,
		accessTokenExpiration: locals.accessTokenExpiration ?? null
	};
};
