/**
 * Portal layout guard. The portal is tenant-only: every /portal API is scoped to the caller's
 * tenantId claim and returns 403 without one. So we require both authentication AND a tenant
 * context — staff (Admin/Manager/Agent) and owners have no tenantId and would otherwise land on a
 * broken dashboard that 403s on every card, so they are redirected to the staff home instead.
 */

import { redirect } from '@sveltejs/kit';
import type { LayoutServerLoad } from './$types';

export const load: LayoutServerLoad = async ({ locals, url }) => {
	if (!locals.user) {
		const redirectTo = url.pathname + url.search;
		throw redirect(303, `/login?redirectTo=${encodeURIComponent(redirectTo)}`);
	}

	// Only tenants (a non-null tenantId) belong in the portal. Send everyone else (staff/owners) to
	// the staff home rather than render an empty, 403-spamming tenant dashboard.
	if (locals.user.tenantId == null) {
		throw redirect(303, '/');
	}

	return {
		user: locals.user,
		accessToken: locals.accessToken,
		accessTokenExpiration: locals.accessTokenExpiration ?? null
	};
};
