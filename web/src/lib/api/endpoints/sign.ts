/**
 * Public, no-login native e-signature client.
 *
 * These endpoints live under `/api/v1/sign/{token}/*` and are ANONYMOUS on the
 * backend — a signer reaches them via a shareable, single-use, expiring link
 * that encodes their opaque token. We must therefore call them WITHOUT the app's
 * auth cookie or bearer token (mirroring the public rental-application flow).
 *
 * Two callers exist:
 *  - The page's `+page.server.ts` load fetches `GET /sign/{token}` server-side
 *    against the API host (SERVER_API_BASE_URL) so the page renders SSR while
 *    logged-out. It passes the raw SvelteKit `fetch` in.
 *  - The browser fetches the signing actions (sign/decline) through the
 *    same-origin `/api/v1` proxy (API_BASE_URL) so a logged-out signer on a
 *    phone can submit without any session.
 *
 * The PDF itself is reached by the browser as a plain same-origin URL
 * (`documentUrlFor(token)`) embeddable in an <iframe>/<object> — no JS fetch
 * needed, no auth header.
 */

import { API_BASE_URL } from '$lib/config';

/** The signer's per-signer status on the wire. */
export type SignerStatus = 'Pending' | 'Viewed' | 'Signed' | 'Declined' | string;

/** The overall request status on the wire. */
export type SignRequestStatus =
	| 'Sent'
	| 'Viewed'
	| 'PartiallySigned'
	| 'Completed'
	| 'Declined'
	| 'Voided'
	| string;

/** GET /api/v1/sign/{token} — everything the signing page needs to render. */
export interface SignPackageResponse {
	signerName: string;
	signerEmail: string;
	subject: string | null;
	documentName: string;
	senderName: string;
	signerStatus: SignerStatus;
	requestStatus: SignRequestStatus;
	alreadySigned: boolean;
	/** Relative URL (e.g. `/api/v1/sign/{token}/document`) to stream the PDF. */
	documentUrl: string;
	consentDisclosure: string;
	testId?: string;
}

/** Body for POST /api/v1/sign/{token}. */
export interface SubmitSignatureBody {
	consent: boolean;
	signatureType: 'Typed' | 'Drawn';
	/** Required when signatureType === 'Typed'. */
	typedName?: string;
	/** PNG data URL; required when signatureType === 'Drawn'. */
	drawnImage?: string;
}

/** Body for POST /api/v1/sign/{token}/decline. */
export interface DeclineSignatureBody {
	reason?: string;
}

/** Result returned after a sign/decline action. */
export interface SignActionResponse {
	signerStatus: SignerStatus;
	requestStatus: SignRequestStatus;
	requestCompleted: boolean;
	testId?: string;
}

/**
 * Thrown for any non-2xx public response. `status` lets callers special-case the
 * meaningful ones: 404 (unknown token / invalid link) and 410 (expired, used, or
 * finalized — the link is no longer usable).
 */
export class SignApiError extends Error {
	constructor(
		public status: number,
		message: string
	) {
		super(message);
		this.name = 'SignApiError';
	}
}

const SIGN_FETCH_TIMEOUT_MS = 30_000;

/**
 * Browser-side same-origin URL the <iframe>/<object>/download link can point at.
 * The backend serves this anonymously (inline PDF) and the same-origin proxy
 * forwards it, so no auth header or fetch is required.
 */
export function documentUrlFor(token: string): string {
	return `${API_BASE_URL}/sign/${encodeURIComponent(token)}/document`;
}

/** Pull a human-friendly message off an error response body. */
async function readError(response: Response): Promise<string> {
	try {
		const body = await response.json();
		if (body && typeof body === 'object') {
			const b = body as Record<string, unknown>;
			if (typeof b.error === 'string') return b.error;
			if (
				b.error &&
				typeof b.error === 'object' &&
				typeof (b.error as Record<string, unknown>).message === 'string'
			) {
				return (b.error as Record<string, string>).message;
			}
			if (typeof b.message === 'string') return b.message;
			if (typeof b.title === 'string') return b.title;
		}
	} catch {
		// non-JSON body
	}
	if (response.status === 404) return 'This signing link is invalid.';
	if (response.status === 410) return 'This signing link has expired or has already been used.';
	return `Request failed (${response.status}).`;
}

/** A `fetch`-like signature so server loads can pass SvelteKit's `fetch`. */
type FetchLike = typeof fetch;

/**
 * Fetch the signing package. The server load passes its own `fetch` + an
 * absolute `baseUrl` (the API host) so this runs logged-out during SSR; in the
 * browser it defaults to the same-origin proxy.
 *
 * Note: the backend marks the signer "Viewed" as a side effect of this GET.
 */
export async function getSignPackage(
	token: string,
	opts: { fetch?: FetchLike; baseUrl?: string } = {}
): Promise<SignPackageResponse> {
	const doFetch = opts.fetch ?? fetch;
	const base = opts.baseUrl ?? API_BASE_URL;
	const response = await doFetch(`${base}/sign/${encodeURIComponent(token)}`, {
		method: 'GET',
		headers: { Accept: 'application/json' }
	});
	if (!response.ok) {
		throw new SignApiError(response.status, await readError(response));
	}
	return response.json() as Promise<SignPackageResponse>;
}

/** Browser-side fetch with a timeout, anonymous (no auth header / credentials). */
async function signFetch(endpoint: string, init: RequestInit): Promise<Response> {
	const controller = new AbortController();
	const timeout = setTimeout(() => controller.abort(), SIGN_FETCH_TIMEOUT_MS);
	try {
		return await fetch(`${API_BASE_URL}${endpoint}`, { ...init, signal: controller.signal });
	} catch (err) {
		if (err instanceof DOMException && err.name === 'AbortError') {
			throw new SignApiError(408, 'The request took too long. Please try again.');
		}
		throw err;
	} finally {
		clearTimeout(timeout);
	}
}

/** POST the captured signature + consent. Browser-only (called on submit). */
export async function submitSignature(
	token: string,
	body: SubmitSignatureBody
): Promise<SignActionResponse> {
	const response = await signFetch(`/sign/${encodeURIComponent(token)}`, {
		method: 'POST',
		headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
		body: JSON.stringify(body)
	});
	if (!response.ok) {
		throw new SignApiError(response.status, await readError(response));
	}
	return response.json() as Promise<SignActionResponse>;
}

/** POST a decline (with an optional reason). Browser-only. */
export async function declineSignature(
	token: string,
	body: DeclineSignatureBody = {}
): Promise<SignActionResponse> {
	const response = await signFetch(`/sign/${encodeURIComponent(token)}/decline`, {
		method: 'POST',
		headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
		body: JSON.stringify(body)
	});
	if (!response.ok) {
		throw new SignApiError(response.status, await readError(response));
	}
	return response.json() as Promise<SignActionResponse>;
}
