/** Tenant-experience layout guard. Record access remains server-authoritative. */

import { redirect } from '@sveltejs/kit';
import { serverGet } from '$lib/api/server-fetch';
import type { LayoutServerLoad } from './$types';

interface PortalAccessState {
	hasActiveTenantAccess: boolean;
}

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

	const isUnlinkedRoute = url.pathname === '/portal/unlinked';
	if (locals.accessToken) {
		const state = await serverGet<PortalAccessState>('/portal/access-state', locals.accessToken);
		if (state.data?.hasActiveTenantAccess) {
			if (isUnlinkedRoute) {
				throw redirect(303, '/portal');
			}
		} else if (state.status === 403 || state.data?.hasActiveTenantAccess === false) {
			if (!isUnlinkedRoute) {
				throw redirect(303, '/portal/unlinked');
			}
		}
	}

	return {
		user: locals.user,
		accessToken: locals.accessToken,
		accessTokenExpiration: locals.accessTokenExpiration ?? null,
		access: locals.access
	};
};
