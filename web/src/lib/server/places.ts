/**
 * Server-side Google Places (New) helper.
 *
 * The API key lives ONLY here (server tier) — it is never shipped to the client.
 * The browser talks to our same-origin endpoints under `/places/*`, which call
 * Google with the key. Mirrors the same "key stays on the server" posture as the
 * token-refresh proxy.
 *
 * Enable the **Places API (New)** on the Google Cloud project and set
 * `GOOGLE_PLACES_API_KEY` (web tier env / box `.env`). When the key is absent the
 * feature self-gates OFF and the address fields stay plain manual-entry inputs.
 */
import { env } from '$env/dynamic/private';

const AUTOCOMPLETE_URL = 'https://places.googleapis.com/v1/places:autocomplete';
const DETAILS_URL = 'https://places.googleapis.com/v1/places';

/** True when an API key is configured. Used to gate the feature without leaking the key. */
export function isPlacesEnabled(): boolean {
	return Boolean(env.GOOGLE_PLACES_API_KEY);
}

export interface PlaceSuggestion {
	placeId: string;
	/** Primary line, e.g. "123 Main St". */
	primary: string;
	/** Secondary line, e.g. "Columbus, OH, USA". */
	secondary: string;
}

export interface ResolvedAddress {
	line1: string;
	city: string;
	state: string;
	zip: string;
}

/**
 * Address-biased autocomplete. Returns [] when disabled or on any upstream error
 * so the caller can silently fall back to manual entry.
 */
export async function placesAutocomplete(
	input: string,
	sessionToken: string | undefined,
	fetchFn: typeof fetch = fetch
): Promise<PlaceSuggestion[]> {
	const key = env.GOOGLE_PLACES_API_KEY;
	if (!key || input.trim().length < 3) return [];

	const body: Record<string, unknown> = {
		input,
		includedRegionCodes: ['us'],
		// Bias toward street addresses (not businesses/POIs) for property/owner forms.
		includedPrimaryTypes: ['street_address', 'premise', 'subpremise', 'route']
	};
	if (sessionToken) body.sessionToken = sessionToken;

	const res = await fetchFn(AUTOCOMPLETE_URL, {
		method: 'POST',
		headers: {
			'Content-Type': 'application/json',
			'X-Goog-Api-Key': key,
			'X-Goog-FieldMask':
				'suggestions.placePrediction.placeId,suggestions.placePrediction.structuredFormat.mainText.text,suggestions.placePrediction.structuredFormat.secondaryText.text'
		},
		body: JSON.stringify(body)
	});

	if (!res.ok) {
		console.error('[places] autocomplete failed', res.status, await safeText(res));
		return [];
	}

	const data = (await res.json()) as {
		suggestions?: Array<{
			placePrediction?: {
				placeId?: string;
				structuredFormat?: {
					mainText?: { text?: string };
					secondaryText?: { text?: string };
				};
			};
		}>;
	};

	return (data.suggestions ?? [])
		.map((s) => s.placePrediction)
		.filter((p): p is NonNullable<typeof p> => Boolean(p?.placeId))
		.map((p) => ({
			placeId: p.placeId as string,
			primary: p.structuredFormat?.mainText?.text ?? '',
			secondary: p.structuredFormat?.secondaryText?.text ?? ''
		}));
}

/**
 * Resolve a placeId into structured street/city/state/zip. The sessionToken should
 * match the one used for the autocomplete calls so Google bills it as one session.
 * Returns null when disabled or on error.
 */
export async function placeDetails(
	placeId: string,
	sessionToken: string | undefined,
	fetchFn: typeof fetch = fetch
): Promise<ResolvedAddress | null> {
	const key = env.GOOGLE_PLACES_API_KEY;
	if (!key || !placeId) return null;

	const url = new URL(`${DETAILS_URL}/${encodeURIComponent(placeId)}`);
	if (sessionToken) url.searchParams.set('sessionToken', sessionToken);

	const res = await fetchFn(url, {
		headers: {
			'X-Goog-Api-Key': key,
			'X-Goog-FieldMask': 'addressComponents'
		}
	});

	if (!res.ok) {
		console.error('[places] details failed', res.status, await safeText(res));
		return null;
	}

	const data = (await res.json()) as {
		addressComponents?: Array<{ longText?: string; shortText?: string; types?: string[] }>;
	};
	return parseAddressComponents(data.addressComponents ?? []);
}

/** Map Google address components into our line1/city/state/zip shape. */
function parseAddressComponents(
	components: Array<{ longText?: string; shortText?: string; types?: string[] }>
): ResolvedAddress {
	const get = (type: string) => components.find((c) => c.types?.includes(type));

	const streetNumber = get('street_number')?.longText ?? '';
	const route = get('route')?.longText ?? '';
	const line1 = [streetNumber, route].filter(Boolean).join(' ').trim();

	// City: locality is the common case; fall back to postal_town / sublocality for
	// places that don't carry a locality component.
	const city =
		get('locality')?.longText ??
		get('postal_town')?.longText ??
		get('sublocality_level_1')?.longText ??
		get('sublocality')?.longText ??
		get('administrative_area_level_2')?.longText ??
		'';

	const state = get('administrative_area_level_1')?.shortText ?? '';
	const zip = get('postal_code')?.longText ?? '';

	return { line1, city, state, zip };
}

async function safeText(res: Response): Promise<string> {
	try {
		return (await res.text()).slice(0, 500);
	} catch {
		return '<no body>';
	}
}
