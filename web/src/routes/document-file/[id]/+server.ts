import { error } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { SERVER_API_BASE_URL } from '$lib/server/config';
import { getAccessToken } from '$lib/server/auth-cookies';

const INLINE_SAFE = new Set([
	'image/jpeg',
	'image/png',
	'image/webp',
	'image/gif',
	'image/heic',
	'application/pdf'
]);

/**
 * Same-origin, cookie-authenticated proxy for a specific StoredFile row.
 * Unit document lists can contain several files attached to the same lease/work-order,
 * so record-level proxies such as /lease-file/{leaseId} cannot identify the selected file.
 */
export const GET: RequestHandler = async ({ params, cookies, url }) => {
	const token = getAccessToken(cookies);
	if (!token) {
		throw error(401, 'Unauthorized');
	}

	const thumb = url.searchParams.get('thumb');
	const upstreamUrl = `${SERVER_API_BASE_URL}/documents/${params.id}/file${thumb ? '?thumb=true' : ''}`;

	const upstream = await fetch(upstreamUrl, {
		headers: { Authorization: `Bearer ${token}` }
	});

	if (!upstream.ok || !upstream.body) {
		throw error(upstream.status === 404 ? 404 : 502, 'Document not available');
	}

	const upstreamType = (upstream.headers.get('content-type') ?? '').split(';')[0].trim().toLowerCase();
	const isSafe = INLINE_SAFE.has(upstreamType);

	const headers = new Headers();
	headers.set('content-type', isSafe ? upstreamType : 'application/octet-stream');
	const upstreamDisposition = upstream.headers.get('content-disposition');
	headers.set(
		'content-disposition',
		upstreamDisposition ?? `${isSafe ? 'inline' : 'attachment'}; filename="document-${params.id}"`
	);
	headers.set('x-content-type-options', 'nosniff');
	headers.set('cache-control', 'private, max-age=86400');

	return new Response(upstream.body, { status: 200, headers });
};
