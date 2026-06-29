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

	// Only tenants belong in the portal; send everyone else (staff/owners) to the staff home rather
	// than render an empty, 403-spamming tenant dashboard.
	//
	// IMPORTANT: this MUST mirror the (protected) guard's criterion EXACTLY (which sends
	// `Tenant && !staff` users to /portal). Keying this off `tenantId` while (protected) keyed off
	// role caused a /portal <-> / redirect loop (ERR_TOO_MANY_REDIRECTS) for any Tenant-role user
	// whose `tenantId` was momentarily null (e.g. the auth-resilience token decode, whose token
	// carries no tenantId claim). Using the same role test makes the two guards perfect complements,
	// so no user can ever loop.
	const isPortalUser =
		locals.user.roles?.includes('Tenant') &&
		!locals.user.roles.some((r) => ['Admin', 'Manager', 'Agent'].includes(r));
	if (!isPortalUser) {
		throw redirect(303, '/');
	}

	return {
		user: locals.user,
		accessToken: locals.accessToken,
		accessTokenExpiration: locals.accessTokenExpiration ?? null
	};
};
