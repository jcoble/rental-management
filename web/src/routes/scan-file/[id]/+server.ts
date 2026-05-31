import { error } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { SERVER_API_BASE_URL } from '$lib/server/config';

// Only these render inline; anything else downloads as octet-stream so an uploaded
// html/svg can't execute on the app origin (defense-in-depth; the API does this too).
const INLINE_SAFE = new Set([
	'image/jpeg',
	'image/png',
	'image/webp',
	'image/gif',
	'image/heic',
	'application/pdf'
]);

/**
 * Same-origin, cookie-authenticated proxy for scan file previews.
 *
 * The API's `/scans/{id}/file` endpoint requires a JWT bearer, which a browser
 * `<img>`/`<iframe>` cannot attach — so a direct preview URL 401s and the panel renders
 * blank. We read the first-party `access_token` cookie here and forward it as a bearer
 * using the GLOBAL fetch (the same approach the login/logout server actions use — the
 * SvelteKit event `fetch` does not reliably carry an Authorization header to the
 * cross-origin API). The file is streamed back on our own origin.
 */
export const GET: RequestHandler = async ({ params, cookies }) => {
	const token = cookies.get('access_token');
	if (!token) {
		throw error(401, 'Unauthorized');
	}

	const upstream = await fetch(`${SERVER_API_BASE_URL}/scans/${params.id}/file`, {
		headers: { Authorization: `Bearer ${token}` }
	});

	if (!upstream.ok || !upstream.body) {
		throw error(upstream.status === 404 ? 404 : 502, 'Scan file not available');
	}

	const upstreamType = (upstream.headers.get('content-type') ?? '').split(';')[0].trim().toLowerCase();
	const isSafe = INLINE_SAFE.has(upstreamType);

	const headers = new Headers();
	headers.set('content-type', isSafe ? upstreamType : 'application/octet-stream');
	headers.set('content-disposition', `${isSafe ? 'inline' : 'attachment'}; filename="scan-${params.id}"`);
	headers.set('x-content-type-options', 'nosniff');
	headers.set('cache-control', 'private, max-age=60');

	return new Response(upstream.body, { status: 200, headers });
};
