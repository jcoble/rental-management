import { redirect } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import { isPlatformAdmin } from '$lib/server/platform-admin';

export const load: PageServerLoad = ({ locals }) => {
	if (!isPlatformAdmin(locals.user)) {
		throw redirect(303, '/audit');
	}

	return {};
};
