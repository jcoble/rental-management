import { SERVER_API_BASE_URL } from '$lib/server/config';
import type { DocsIndex } from '$lib/api/endpoints/docs';
import type { LayoutServerLoad } from './$types';

/**
 * The docs index powers the persistent left navigation sidebar shared across every
 * docs page. Fail-soft: if the docs service is unreachable, render without the
 * sidebar rather than erroring the whole surface (the page loaders handle the
 * hard error for their own content).
 */
export const load: LayoutServerLoad = async ({ fetch }) => {
	try {
		const res = await fetch(`${SERVER_API_BASE_URL}/docs`);
		if (res.ok) return { index: (await res.json()) as DocsIndex };
	} catch {
		/* ignore — fall through to empty index */
	}
	return { index: { categories: [] } as DocsIndex };
};
