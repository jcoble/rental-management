/**
 * Auth state — Svelte 5 runes, seeded from the server (root +layout.server.ts).
 *
 * The httpOnly cookies are the source of truth for the session; this store is
 * the client-side mirror used for rendering + outgoing Authorization headers.
 * It no longer touches localStorage (cookies handle persistence/SSR).
 */

import { browser } from '$app/environment';
import { goto } from '$app/navigation';
import type { AccessEnvelope, User, WorkspaceExperience } from '$lib/types/user';
import { capabilityKeysForExperience } from '$lib/types/user';
import { realNow } from '$lib/dev/real-time';

let user = $state<User | null>(null);
let accessToken = $state<string | null>(null);
let accessTokenExpiration = $state<Date | null>(null);
let accessEnvelope = $state<AccessEnvelope | null>(null);
let activeExperience = $state<WorkspaceExperience | null>(null);
let isLoading = $state(true);
// Resolved server-side from the PLATFORM_ADMIN_EMAILS allowlist (root +layout.server.ts).
// There is no super-admin role; this boolean gates the platform-operator shell (F6/TSK-212).
let platformAdmin = $state(false);
const isAuthenticated = $derived(!!user && !!accessToken);

/**
 * Client-only write guard (L-13). This is module-scoped state; under adapter-node the server
 * shares module scope across all concurrent requests, so a server-side write would bleed one
 * user's identity into another's SSR render. Every mutator routes through here. Reads stay
 * unguarded — they're consumed during SSR render and safely return the null/default seed.
 */
function assertClientWrite(fn: string): void {
	if (!browser) {
		throw new Error(`${fn} must not be called on the server (auth store is client-only).`);
	}
}

/** Optional hook fired when auth is cleared (e.g. to disconnect SignalR). */
let onAuthClearedCallback: (() => void) | null = null;

export function setOnAuthCleared(callback: (() => void) | null) {
	onAuthClearedCallback = callback;
}

/** Seed the store from server-provided layout data. */
export function initAuth(
	initialUser: User | null,
	initialToken: string | null,
	expiration: Date | null,
	initialAccess: AccessEnvelope | null,
	initialPlatformAdmin = false
) {
	assertClientWrite('initAuth');
	user = initialUser;
	accessToken = initialToken;
	accessTokenExpiration = expiration;
	accessEnvelope = initialAccess;
	activeExperience = initialAccess?.selectedContext.activeExperience ?? null;
	platformAdmin = initialPlatformAdmin;
	isLoading = false;
}

/** Set auth after a successful login (client-side). */
export function setAuth(newUser: User, newToken: string, expiration: Date, access: AccessEnvelope) {
	assertClientWrite('setAuth');
	user = newUser;
	accessToken = newToken;
	accessTokenExpiration = expiration;
	accessEnvelope = access;
	activeExperience = access.selectedContext.activeExperience;
	isLoading = false;
}

/** Update only the token (after a refresh). */
export function updateToken(newToken: string, expiration: Date, access?: AccessEnvelope) {
	assertClientWrite('updateToken');
	accessToken = newToken;
	accessTokenExpiration = expiration;
	if (access) setAccessEnvelope(access);
}

export function setAccessEnvelope(access: AccessEnvelope) {
	assertClientWrite('setAccessEnvelope');
	accessEnvelope = access;
	if (!activeExperience || !access.availableExperiences.includes(activeExperience)) {
		activeExperience = access.selectedContext.activeExperience;
	}
}

export function selectExperience(experience: WorkspaceExperience) {
	assertClientWrite('selectExperience');
	if (!accessEnvelope?.availableExperiences.includes(experience)) {
		throw new Error('That experience is not available in the selected workspace.');
	}
	activeExperience = experience;
}

export function currentCapabilities(): ReadonlySet<string> {
	return capabilityKeysForExperience(accessEnvelope, activeExperience);
}

export function hasCapability(...keys: string[]): boolean {
	const available = currentCapabilities();
	return keys.some((key) => available.has(key));
}

/**
 * Tear down just the client-side auth mirror (and fire the cleared callback, e.g. to disconnect
 * SignalR), WITHOUT navigating. Used by {@link clearAuth} and by callers that own the redirect
 * themselves (e.g. AppShell's Sign Out, which hands off to /logout with a full-document navigation
 * so the httpOnly cookie deletions can't be raced by a client-side goto).
 */
export function clearAuthState() {
	assertClientWrite('clearAuthState');
	user = null;
	accessToken = null;
	accessTokenExpiration = null;
	accessEnvelope = null;
	activeExperience = null;
	platformAdmin = false;
	isLoading = false;

	if (browser && onAuthClearedCallback) {
		try {
			onAuthClearedCallback();
		} catch {
			/* best effort */
		}
	}
}

/**
 * Clear auth state on logout / session expiry. In the browser, fires the
 * cleared callback and navigates to /logout.
 *
 * We go to /logout (not /login) on purpose: the access/refresh tokens are
 * httpOnly cookies the client can't delete, so navigating straight to /login
 * would leave a still-valid access-token cookie behind — the /login load would
 * see locals.user and bounce the user back to the dashboard, looping until the
 * access token expires. /logout clears all session cookies (and best-effort
 * revokes the refresh token) server-side, then redirects to /login cleanly.
 */
export function clearAuth() {
	clearAuthState();
	if (browser) {
		void goto('/logout');
	}
}

/**
 * True if the token is expired or expires within `bufferSeconds`.
 *
 * M3: compares against {@link realNow}, NOT `Date.now()`. The dev sim-clock shim replaces the global
 * `Date`, so a bare `Date.now()` here would return simulated time — advancing the sim clock would make
 * every live token read as expired, triggering refresh storms and refresh-family revocation. The server
 * issues/validates JWTs on the real clock, so this client-side gate must use real time too. (The stored
 * `accessTokenExpiration` is a `new Date(<iso string>)`, which the shim passes through unchanged.)
 */
export function isTokenExpired(bufferSeconds = 60): boolean {
	if (!accessTokenExpiration) return true;
	const buffer = bufferSeconds * 1000;
	return accessTokenExpiration.getTime() - realNow() < buffer;
}

/** Reactive accessor for components. */
export function getAuthState() {
	return {
		get user() {
			return user;
		},
		get accessToken() {
			return accessToken;
		},
		get accessTokenExpiration() {
			return accessTokenExpiration;
		},
		get isLoading() {
			return isLoading;
		},
		get isAuthenticated() {
			return isAuthenticated;
		},
		get accessEnvelope() {
			return accessEnvelope;
		},
		get activeExperience() {
			return activeExperience;
		}
	};
}

// ---- Convenience accessors bound to the current session ----

export function getCurrentUser(): User | null {
	return user;
}

/** Replace the current user (e.g. after a fresh /auth/me fetch). */
export function setCurrentUser(newUser: User | null) {
	assertClientWrite('setCurrentUser');
	user = newUser;
	isLoading = false;
}

/** Platform super-admin (email allowlist), gates the operator shell (Engine Health, etc.). */
export function currentUserIsPlatformAdmin(): boolean {
	return platformAdmin;
}
