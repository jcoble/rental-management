/**
 * Public marketing landing page.
 *
 * This is the unauthenticated home. Logged-in users are bounced to their app
 * (the dashboard / portal) so the landing only ever shows to visitors.
 */

import { redirect } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ locals }) => {
	if (locals.user) {
		const activeExperience = locals.access?.selectedContext.activeExperience;
		throw redirect(303, activeExperience === 'Tenant' ? '/portal' : '/');
	}

	return {};
};
