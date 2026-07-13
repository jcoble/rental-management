/**
 * Public marketing landing page.
 *
 * This is the unauthenticated home. Logged-in users are bounced to their app
 * (the dashboard / portal) so the landing only ever shows to visitors.
 */

import { redirect } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import { safeLandingForAccess } from '$lib/auth/experience-policy';

export const load: PageServerLoad = async ({ locals }) => {
	if (locals.user) {
		throw redirect(303, locals.access ? (safeLandingForAccess(locals.access) ?? '/logout') : '/logout');
	}

	return {};
};
