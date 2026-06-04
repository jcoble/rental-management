/**
 * Public documentation (Knowledge Base) API.
 *
 * The docs endpoints are ANONYMOUS on the backend — no auth, no portfolio scope.
 * They power the public /docs site (read logged-out) and the chatbot citations.
 *
 * Server-side loads (`+page.server.ts`) fetch these directly against the API
 * host using SERVER_API_BASE_URL so they work without a cookie/session and are
 * SSR/SEO friendly. The plain types here are shared by both server and client.
 */

/** One article as it appears in the index (no body). */
export interface DocArticleSummary {
	slug: string;
	title: string;
	category: string;
	summary: string;
	order: number;
}

/** A category bucket in the docs index. */
export interface DocCategory {
	category: string;
	articles: DocArticleSummary[];
}

/** GET /docs response shape. */
export interface DocsIndex {
	categories: DocCategory[];
}

/** Full article including the raw markdown body. GET /docs/{slug}. */
export interface DocArticle {
	slug: string;
	title: string;
	category: string;
	summary: string;
	order: number;
	keywords: string[];
	body: string;
}

/** A docs citation attached to an AI answer (`source === 'Docs'`). */
export interface DocCitation {
	slug: string;
	title: string;
	category: string;
}
