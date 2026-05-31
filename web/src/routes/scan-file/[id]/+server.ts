import { error } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { SERVER_API_BASE_URL } from '$lib/server/config';

/**
 * Same-origin, cookie-authenticated proxy for scan file previews.
 *
 * The API's `/scans/{id}/file` endpoint requires a JWT bearer token, which a
 * browser `<img>`/`<iframe>` cannot attach — so a direct preview URL 401s and the
 * document panel renders blank. Here we run server-side (the access token is on
 * `locals` from hooks.server.ts), fetch the file from the API with the bearer, and
 * stream it back to the browser on our own origin. The review page points its
 * `<img>`/`<iframe>` at `/scan-file/{id}`.
 */
export const GET: RequestHandler = async ({ params, locals, fetch }) => {
	if (!locals.user || !locals.accessToken) {
		throw error(401, 'Unauthorized');
	}

	const upstream = await fetch(`${SERVER_API_BASE_URL}/scans/${params.id}/file`, {
		headers: { Authorization: `Bearer ${locals.accessToken}` }
	});

	if (!upstream.ok || !upstream.body) {
		throw error(upstream.status === 404 ? 404 : 502, 'Scan file not available');
	}

	// Defense-in-depth: this route serves on the APP origin, so it must not trust the
	// upstream content-type. Render only known-safe types inline; force everything else to
	// download as octet-stream so an uploaded html/svg can't execute here. (The API already
	// neutralizes risky types, but the proxy must not depend on that.)
	const INLINE_SAFE = new Set([
		'image/jpeg', 'image/png', 'image/webp', 'image/gif', 'image/heic', 'application/pdf'
	]);
	const upstreamType = (upstream.headers.get('content-type') ?? '').split(';')[0].trim().toLowerCase();
	const isSafe = INLINE_SAFE.has(upstreamType);

	const headers = new Headers();
	headers.set('content-type', isSafe ? upstreamType : 'application/octet-stream');
	headers.set('content-disposition', `${isSafe ? 'inline' : 'attachment'}; filename="scan-${params.id}"`);
	headers.set('x-content-type-options', 'nosniff');
	headers.set('cache-control', 'private, max-age=60');

	return new Response(upstream.body, { status: 200, headers });
};
