/**
 * Public signing-page loader.
 *
 * Fetches the anonymous `GET /sign/{token}` package directly from the API host
 * (SERVER_API_BASE_URL) so the page renders server-side while LOGGED OUT — this
 * route lives in the unguarded `(public)` group and is opened by a signer (often
 * on a phone) who has no session.
 *
 * Rather than throwing a generic SvelteKit error for the "link not usable"
 * cases, we return a discriminated state so `+page.svelte` can render clean,
 * branded screens:
 *   - 404 → `invalid`  (unknown token — the link is wrong/garbled)
 *   - 410 → `expired`  (expired, already used, or the request is finalized)
 *   - 2xx → `ok` + the package
 * Genuinely broken infrastructure (network/5xx) still throws so the error page
 * shows.
 *
 * The browser-facing document URL is built same-origin (`/api/v1/...`) so the
 * <iframe>/<object> and download link load the PDF through the proxy without any
 * auth header.
 */

import { error } from '@sveltejs/kit';
import { SERVER_API_BASE_URL } from '$lib/server/config';
import { getSignPackage, SignApiError, type SignPackageResponse } from '$lib/api/endpoints/sign';
import type { PageServerLoad } from './$types';

export type SignLoadState =
	| { state: 'ok'; token: string; pkg: SignPackageResponse }
	| { state: 'invalid'; token: string }
	| { state: 'expired'; token: string };

export const load: PageServerLoad = async ({ params, fetch }): Promise<SignLoadState> => {
	const token = params.token;

	try {
		const pkg = await getSignPackage(token, { fetch, baseUrl: SERVER_API_BASE_URL });
		return { state: 'ok', token, pkg };
	} catch (err) {
		if (err instanceof SignApiError) {
			if (err.status === 404) return { state: 'invalid', token };
			// 410 Gone — expired / already used / request finalized. Also treat any
			// other 4xx as "not usable" rather than a scary error page.
			if (err.status === 410 || (err.status >= 400 && err.status < 500)) {
				return { state: 'expired', token };
			}
		}
		// Network failure or 5xx — the signing service is unavailable right now.
		throw error(502, 'The signing service is unavailable right now. Please try again shortly.');
	}
};
