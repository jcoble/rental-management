import { serverGet } from '$lib/api/server-fetch';
import type { DocsIndex } from '$lib/api/endpoints/docs';
import type { LayoutServerLoad } from './$types';

/**
 * The docs index powers the persistent left navigation sidebar shared across every
 * docs page. Fail-soft: if the docs service is unreachable, render without the
 * sidebar rather than erroring the whole surface (the page loaders handle the
 * hard error for their own content).
 */
export const load: LayoutServerLoad = async () => {
	const result = await serverGet<DocsIndex>('/docs');
	if (result.data) return { index: result.data };
	return { index: { categories: [] } as DocsIndex };
};
