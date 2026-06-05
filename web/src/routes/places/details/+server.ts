/**
 * Same-origin place-details proxy. Resolves a picked suggestion into structured
 * street/city/state/zip. Keeps the Google Places key server-side.
 *
 * GET /places/details?placeId=<id>&session=<token>
 *   → { line1, city, state, zip } | 404 when unresolved/disabled
 *
 * The session token should match the one used for the autocomplete calls so Google
 * bills the lookup as a single session.
 */
import { json, error } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { isPlacesEnabled, placeDetails } from '$lib/server/places';

export const GET: RequestHandler = async ({ url, fetch }) => {
	if (!isPlacesEnabled()) {
		throw error(404, 'Address lookup is not enabled');
	}

	const placeId = url.searchParams.get('placeId')?.trim();
	const session = url.searchParams.get('session') ?? undefined;
	if (!placeId) {
		throw error(400, 'Missing placeId');
	}

	const resolved = await placeDetails(placeId, session, fetch);
	if (!resolved) {
		throw error(404, 'Could not resolve address');
	}

	return json(resolved);
};
