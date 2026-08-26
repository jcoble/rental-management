/**
 * Public docs index loader.
 *
 * Fetches the anonymous GET /docs index directly from the API host. Runs
 * unauthenticated (no cookie required), server-side for SSR/SEO. The docs
 * endpoints are public on the backend, so no Authorization header is needed —
 * handleFetch simply forwards the request.
 */

import { serverGet } from '$lib/api/server-fetch';
import type { DocsIndex } from '$lib/api/endpoints/docs';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async () => {
	const result = await serverGet<DocsIndex>('/docs');
	if (result.data) {
		return { index: result.data, unavailable: false };
	}
	// Fail-soft: when the docs service is unreachable (or returns non-OK), render a
	// friendly "being set up / temporarily unavailable" state instead of a hard 5xx
	// error page. The footer + nav "Docs" link should always land somewhere usable.
	return { index: { categories: [] } as DocsIndex, unavailable: true };
};
