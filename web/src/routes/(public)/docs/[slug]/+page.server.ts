/**
 * Public docs article loader.
 *
 * Fetches the article (raw markdown body) and the index (for the sidebar +
 * prev/next links) from the anonymous API, then renders the markdown to HTML
 * with `marked` server-side. Rendering on the server keeps `marked` out of the
 * client bundle and makes the article SSR/SEO friendly while working logged-out.
 *
 * The rendered HTML is run through a real allowlist sanitizer (sanitize-html)
 * before it reaches the `{@html}` sink. A regex scrub of <script>/<style>/on*=
 * is well-known bypassable (e.g. <svg><animate onbegin>, attribute-splitting,
 * data:/srcdoc payloads); even though the body comes from our own Knowledge
 * Base, this is the load-bearing defense the moment an article can be influenced
 * by an untrusted author (L-14).
 */

import { error } from '@sveltejs/kit';
import { marked } from 'marked';
import sanitizeHtmlLib from 'sanitize-html';
import { SERVER_API_BASE_URL } from '$lib/server/config';
import type { DocArticle, DocsIndex } from '$lib/api/endpoints/docs';
import type { PageServerLoad } from './$types';

/**
 * Allowlist sanitizer for rendered markdown. Tags cover everything `marked` (gfm)
 * emits for our docs — headings, text formatting, lists, links, code/pre, tables,
 * blockquotes, images, hr. Notably absent: <script>, <style>, <iframe> (so srcdoc
 * can't smuggle markup). Schemes are restricted to http/https/mailto for links and
 * http/https for images — `data:`/`javascript:` URIs are dropped. Heading `id`s are
 * injected AFTER this pass (trusted, server-generated slugs), so they don't need to
 * survive sanitization here.
 */
function sanitizeHtml(html: string): string {
	return sanitizeHtmlLib(html, {
		allowedTags: [
			'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
			'p', 'a', 'ul', 'ol', 'li',
			'blockquote', 'code', 'pre', 'em', 'strong', 'del', 's',
			'hr', 'br', 'span',
			'table', 'thead', 'tbody', 'tr', 'th', 'td',
			'img'
		],
		allowedAttributes: {
			a: ['href', 'name', 'target', 'rel'],
			img: ['src', 'alt', 'title'],
			th: ['align'],
			td: ['align']
		},
		allowedSchemes: ['http', 'https', 'mailto'],
		allowedSchemesByTag: { img: ['http', 'https'] },
		// Drop protocol-relative (//evil.com) URLs.
		allowProtocolRelative: false,
		// Strip the CONTENTS of these disallowed tags too, not just the tags.
		nonTextTags: ['style', 'script', 'textarea', 'option', 'noscript']
	});
}

export interface DocTocItem {
	id: string;
	text: string;
	level: 2 | 3;
}

function decodeEntities(s: string): string {
	return s
		.replace(/<[^>]+>/g, '')
		.replace(/&amp;/g, '&')
		.replace(/&lt;/g, '<')
		.replace(/&gt;/g, '>')
		.replace(/&quot;/g, '"')
		.replace(/&#39;/g, "'")
		.trim();
}

function slugify(text: string): string {
	return text
		.toLowerCase()
		.replace(/[^\w\s-]/g, '')
		.replace(/\s+/g, '-')
		.replace(/-+/g, '-')
		.replace(/^-|-$/g, '');
}

/**
 * Injects stable `id`s onto h2/h3 headings in the rendered HTML and returns the matching
 * "On this page" table of contents. `marked` emits id-less headings, so the TOC rail and
 * in-page anchors both rely on this.
 */
function injectHeadingIdsAndToc(html: string): { html: string; toc: DocTocItem[] } {
	const toc: DocTocItem[] = [];
	const used = new Set<string>();

	const out = html.replace(/<h([23])>([\s\S]*?)<\/h\1>/g, (_m, levelStr: string, inner: string) => {
		const level = Number(levelStr) as 2 | 3;
		const text = decodeEntities(inner);
		let id = slugify(text) || `section-${toc.length + 1}`;
		// De-dupe so repeated headings still get unique anchors.
		if (used.has(id)) {
			let n = 2;
			while (used.has(`${id}-${n}`)) n++;
			id = `${id}-${n}`;
		}
		used.add(id);
		toc.push({ id, text, level });
		return `<h${level} id="${id}">${inner}</h${level}>`;
	});

	return { html: out, toc };
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

	// Render the trusted markdown body to HTML on the server, then add heading anchors + build
	// the "On this page" table of contents.
	const rawHtml = await marked.parse(article.body ?? '', { async: false, gfm: true });
	const { html: bodyHtml, toc } = injectHeadingIdsAndToc(sanitizeHtml(rawHtml));

	// Build a flat, ordered list of articles for prev/next navigation.
	const flat = index.categories.flatMap((c) => c.articles);
	const currentIdx = flat.findIndex((a) => a.slug === slug);
	const prev = currentIdx > 0 ? flat[currentIdx - 1] : null;
	const next = currentIdx >= 0 && currentIdx < flat.length - 1 ? flat[currentIdx + 1] : null;

	return {
		article,
		bodyHtml,
		toc,
		index,
		prev,
		next
	};
};
