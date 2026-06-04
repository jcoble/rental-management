/**
 * Public docs article loader.
 *
 * Fetches the article (raw markdown body) and the index (for the sidebar +
 * prev/next links) from the anonymous API, then renders the markdown to HTML
 * with `marked` server-side. Rendering on the server keeps `marked` out of the
 * client bundle and makes the article SSR/SEO friendly while working logged-out.
 *
 * The content is our own trusted Knowledge Base, so we only do a light strip of
 * <script>/<style>/event-handler attributes rather than a full sanitizer.
 */

import { error } from '@sveltejs/kit';
import { marked } from 'marked';
import { SERVER_API_BASE_URL } from '$lib/server/config';
import type { DocArticle, DocsIndex } from '$lib/api/endpoints/docs';
import type { PageServerLoad } from './$types';

/** Light defense-in-depth scrub for our own trusted markdown output. */
function sanitizeHtml(html: string): string {
	return html
		.replace(/<\s*script[^>]*>[\s\S]*?<\s*\/\s*script\s*>/gi, '')
		.replace(/<\s*style[^>]*>[\s\S]*?<\s*\/\s*style\s*>/gi, '')
		.replace(/\son\w+\s*=\s*("[^"]*"|'[^']*'|[^\s>]+)/gi, '')
		.replace(/(href|src)\s*=\s*("|')\s*javascript:[^"']*\2/gi, '$1=$2#$2');
}

export const load: PageServerLoad = async ({ params, fetch }) => {
	const { slug } = params;

	let articleRes: Response;
	let indexRes: Response;
	try {
		[articleRes, indexRes] = await Promise.all([
			fetch(`${SERVER_API_BASE_URL}/docs/${encodeURIComponent(slug)}`),
			fetch(`${SERVER_API_BASE_URL}/docs`)
		]);
	} catch {
		throw error(502, 'The documentation service is unavailable right now.');
	}

	if (articleRes.status === 404) {
		throw error(404, 'That documentation article could not be found.');
	}
	if (!articleRes.ok) {
		throw error(articleRes.status, 'Could not load this article.');
	}

	const article = (await articleRes.json()) as DocArticle;
	const index = indexRes.ok ? ((await indexRes.json()) as DocsIndex) : { categories: [] };

	// Render the trusted markdown body to HTML on the server.
	const rawHtml = await marked.parse(article.body ?? '', { async: false, gfm: true });
	const bodyHtml = sanitizeHtml(rawHtml);

	// Build a flat, ordered list of articles for prev/next navigation.
	const flat = index.categories.flatMap((c) => c.articles);
	const currentIdx = flat.findIndex((a) => a.slug === slug);
	const prev = currentIdx > 0 ? flat[currentIdx - 1] : null;
	const next = currentIdx >= 0 && currentIdx < flat.length - 1 ? flat[currentIdx + 1] : null;

	return {
		article,
		bodyHtml,
		index,
		prev,
		next
	};
};
