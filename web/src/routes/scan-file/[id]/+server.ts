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
 * blank. The auth hook validates or refreshes the first-party session and exposes the
 * current token on `locals`; this route forwards that token with the global fetch so the
 * file streams back on our own origin.
 */
export const GET: RequestHandler = async ({ params, locals, url }) => {
	const token = locals.accessToken;
	if (!token) {
		throw error(401, 'Unauthorized');
	}

	// Forward an optional `full` flag so callers can request the full-resolution original
	// instead of the default ~1000px thumbnail the API serves for image uploads. The API
	// accepts ?full=1 / ?full=true; anything else falls back to the thumbnail.
	const full = url.searchParams.get('full');
	const upstreamUrl = full
		? `${SERVER_API_BASE_URL}/scans/${params.id}/file?full=${encodeURIComponent(full)}`
		: `${SERVER_API_BASE_URL}/scans/${params.id}/file`;

	const upstream = await fetch(upstreamUrl, {
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
