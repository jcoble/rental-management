/**
 * Decode a first-party access-token's claims WITHOUT verifying its signature.
 *
 * Used only as a transient-failure fallback in hooks.server.ts: when GET /auth/me can't be reached
 * (network/cert error, a 5xx, or a timeout — anything that is NOT a real 401), we still want an
 * already-authenticated user to stay signed in through the blip instead of 303-bouncing every
 * logged-in user to /login on any brief API hiccup (e.g. a deploy restart).
 *
 * Decoding the token locally without signature verification is acceptable here because the token is
 * first-party (we minted it) and short-lived (~15 min), and it is re-validated against the API on
 * the very next request that succeeds. We still REJECT an expired or malformed token via the `exp`
 * claim, so a stale token can never extend a session. This is a best-effort fail-safe bridge, NOT a
 * substitute for /auth/me.
 *
 * No crypto dependency: a JWT payload is just the base64url-encoded middle segment of the token.
 */

import type { User } from '$lib/types/user';

// .NET ClaimTypes.Role URI. The API mints role claims via `new Claim(ClaimTypes.Role, role)`; the
// encoded claim name is either the short "role" or this long URI depending on the
// JwtSecurityTokenHandler outbound map, so we read both to be safe.
const ROLE_CLAIM_URI = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';

type JwtClaims = Record<string, unknown>;

/** base64url-decode + JSON.parse the JWT payload segment; null on any malformation. */
function decodeJwtPayload(token: string): JwtClaims | null {
	const segments = token.split('.');
	if (segments.length !== 3) return null;
	try {
		const json = Buffer.from(segments[1], 'base64url').toString('utf8');
		const parsed: unknown = JSON.parse(json);
		return parsed !== null && typeof parsed === 'object' ? (parsed as JwtClaims) : null;
	} catch {
		return null;
	}
}

function claimString(claims: JwtClaims, key: string): string | null {
	const value = claims[key];
	return typeof value === 'string' ? value : null;
}

/** Read a string-encoded int claim (the token carries int ids as strings) as a number, or null. */
function claimNumber(claims: JwtClaims, key: string): number | null {
	const value = claims[key];
	if (typeof value !== 'string' || value.trim() === '') return null;
	const parsed = Number(value);
	return Number.isFinite(parsed) ? parsed : null;
}

/** Collect role names from either the short "role" claim or the long ClaimTypes.Role URI. */
function rolesFromClaims(claims: JwtClaims): string[] {
	const raw = claims['role'] ?? claims[ROLE_CLAIM_URI];
	if (Array.isArray(raw)) return raw.filter((role): role is string => typeof role === 'string');
	return typeof raw === 'string' ? [raw] : [];
}

/**
 * Reconstruct `locals.user` from a first-party access token, or null when the token is malformed,
 * carries no usable subject, or is EXPIRED (per its `exp` claim, seconds since the epoch). An
 * expired or malformed token is never honored — the caller falls through to the normal guards.
 *
 * @param nowMs current time in ms (injectable for tests; defaults to Date.now()).
 */
export function userFromAccessToken(token: string, nowMs: number = Date.now()): User | null {
	const claims = decodeJwtPayload(token);
	if (!claims) return null;

	// Reject anything without a valid, still-future expiry. (exp is in seconds; compare in ms.)
	const exp = claims['exp'];
	if (typeof exp !== 'number' || exp * 1000 <= nowMs) return null;

	// `sub` carries the int user id, string-encoded. Without a usable id we cannot build a user.
	const id = claimNumber(claims, 'sub');
	if (id === null) return null;

	const email = claimString(claims, 'email') ?? '';
	return {
		id,
		email,
		displayName: claimString(claims, 'displayName') ?? email,
		portfolioId: claimNumber(claims, 'portfolioId'),
		ownerEntityId: claimNumber(claims, 'ownerEntityId'),
		tenantId: claimNumber(claims, 'tenantId'),
		roles: rolesFromClaims(claims),
		// Not carried in the access token. The user is already authenticated and this is only a brief
		// blip-bridge re-validated on the next successful request, so assume verified rather than
		// spuriously gating them. (No route guard currently reads emailVerified.)
		emailVerified: true
	};
}
