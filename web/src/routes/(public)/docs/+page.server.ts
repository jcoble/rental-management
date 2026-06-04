/**
 * Public docs index loader.
 *
 * Fetches the anonymous GET /docs index directly from the API host. Runs
 * unauthenticated (no cookie required), server-side for SSR/SEO. The docs
 * endpoints are public on the backend, so no Authorization header is needed —
 * handleFetch simply forwards the request.
 */

import { error } from '@sveltejs/kit';
import { SERVER_API_BASE_URL } from '$lib/server/config';
import type { DocsIndex } from '$lib/api/endpoints/docs';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ fetch }) => {
	try {
		const res = await fetch(`${SERVER_API_BASE_URL}/docs`);
		if (!res.ok) {
			throw error(res.status, 'Could not load documentation.');
		}
		const index = (await res.json()) as DocsIndex;
		return { index };
	} catch (err) {
		// A thrown SvelteKit `error` has a `status`; re-throw it untouched.
		if (err && typeof err === 'object' && 'status' in err) throw err;
		throw error(502, 'The documentation service is unavailable right now.');
	}
};
