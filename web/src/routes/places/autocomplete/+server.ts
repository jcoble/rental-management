/**
 * Same-origin address autocomplete proxy. Keeps the Google Places key server-side.
 *
 * GET /places/autocomplete?q=<input>&session=<token>
 *   → { enabled: boolean, suggestions: [{ placeId, primary, secondary }] }
 *
 * `enabled:false` tells the client no key is configured, so it stops calling and
 * the address field stays a plain manual input. This path is NOT under `/api`, so
 * neither the Vite dev proxy nor Traefik forward it to the .NET API — SvelteKit
 * serves it directly.
 */
import { json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { isPlacesEnabled, placesAutocomplete } from '$lib/server/places';

export const GET: RequestHandler = async ({ url, fetch }) => {
	if (!isPlacesEnabled()) {
		return json({ enabled: false, suggestions: [] });
	}

	const q = url.searchParams.get('q')?.trim() ?? '';
	const session = url.searchParams.get('session') ?? undefined;
	if (q.length < 3) {
		return json({ enabled: true, suggestions: [] });
	}

	const suggestions = await placesAutocomplete(q, session, fetch);
	return json({ enabled: true, suggestions });
};
