/**
 * Public docs index loader.
 *
 * Fetches the anonymous GET /docs index directly from the API host. Runs
 * unauthenticated (no cookie required), server-side for SSR/SEO. The docs
 * endpoints are public on the backend, so no Authorization header is needed —
 * handleFetch simply forwards the request.
 */

import { SERVER_API_BASE_URL } from '$lib/server/config';
import type { DocsIndex } from '$lib/api/endpoints/docs';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ fetch }) => {
	try {
		const res = await fetch(`${SERVER_API_BASE_URL}/docs`);
		if (res.ok) {
			const index = (await res.json()) as DocsIndex;
			return { index, unavailable: false };
		}
	} catch {
		/* fall through to the graceful empty state below */
	}
	// Fail-soft: when the docs service is unreachable (or returns non-OK), render a
	// friendly "being set up / temporarily unavailable" state instead of a hard 5xx
	// error page. The footer + nav "Docs" link should always land somewhere usable.
	return { index: { categories: [] } as DocsIndex, unavailable: true };
};
