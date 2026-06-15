/**
 * Anti-CSRF `state` handling for the Google OAuth2 authorization-code flow (web/SSR).
 *
 * Without a `state` parameter the flow is open to login-CSRF / authorization-code injection /
 * session fixation: an attacker can complete consent with their own account, capture the `code`,
 * and induce a victim's browser to hit the callback — silently logging the victim INTO the
 * attacker's account. `state` binds the callback to the same browser that started the flow:
 * we mint a random value, store it in a short-lived httpOnly cookie before redirecting to Google,
 * and require the value Google echoes back to match that cookie before exchanging the code.
 */

import type { Cookies } from '@sveltejs/kit';
import { AUTH_COOKIE_NAMES } from './auth-cookies.ts';

const COOKIE_PATH = '/';

/** The OAuth flow is a single round-trip; the state only needs to survive the consent redirect. */
const STATE_TTL_SECONDS = 60 * 10; // 10 minutes

/** Generate a cryptographically-random, URL-safe state token. */
export function generateOAuthState(): string {
	return crypto.randomUUID();
}

/**
 * Persist the freshly-generated state in a short-lived, httpOnly, SameSite=lax cookie so the
 * callback (a top-level GET) can read it back. SameSite=lax is required (the callback is a
 * navigation), and is sufficient here because the value is server-generated and never exposed to JS.
 */
export function setOAuthStateCookie(cookies: Cookies, state: string): void {
	cookies.set(AUTH_COOKIE_NAMES.oauthState, state, {
		path: COOKIE_PATH,
		httpOnly: true,
		secure: true,
		sameSite: 'lax',
		maxAge: STATE_TTL_SECONDS
	});
}

/** Remove the state cookie. Always called on the callback so a value can never be replayed. */
export function clearOAuthStateCookie(cookies: Cookies): void {
	cookies.delete(AUTH_COOKIE_NAMES.oauthState, { path: COOKIE_PATH });
}

/**
 * Constant-time string comparison. Avoids leaking, via timing, how much of the expected state a
 * guessed value matched. Returns false immediately on any nullish/length mismatch.
 */
function timingSafeEqual(a: string | undefined | null, b: string | undefined | null): boolean {
	if (!a || !b || a.length !== b.length) {
		return false;
	}
	let mismatch = 0;
	for (let i = 0; i < a.length; i++) {
		mismatch |= a.charCodeAt(i) ^ b.charCodeAt(i);
	}
	return mismatch === 0;
}

/**
 * Validate the `state` Google echoed back against the value we stored before the redirect.
 * Returns true only when both are present and match. The caller must reject the request and
 * NOT exchange the authorization code when this is false.
 */
export function isValidOAuthState(
	returnedState: string | null | undefined,
	storedState: string | undefined
): boolean {
	return timingSafeEqual(returnedState, storedState);
}
