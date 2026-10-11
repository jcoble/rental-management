/**
 * Root layout server load — seeds auth state for every route.
 *
 * locals.user / locals.accessToken are populated by hooks.server.ts. We pass
 * them to the client so the runes auth store can hydrate without a round-trip.
 */

import type { LayoutServerLoad } from './$types';
import { isPlatformAdmin } from '$lib/server/platform-admin';

export const load: LayoutServerLoad = async ({ locals }) => {
	return {
		user: locals.user,
		accessToken: locals.accessToken,
		accessTokenExpiration: locals.accessTokenExpiration ?? null,
		access: locals.access,
		// Resolved server-side from the PLATFORM_ADMIN_EMAILS allowlist; the client only
		// ever sees this boolean, never the list (F6 / TSK-212).
		isPlatformAdmin: isPlatformAdmin(locals.user)
	};
};
