/**
 * Auth state — Svelte 5 runes, seeded from the server (root +layout.server.ts).
 *
 * The httpOnly cookies are the source of truth for the session; this store is
 * the client-side mirror used for rendering + outgoing Authorization headers.
 * It no longer touches localStorage (cookies handle persistence/SSR).
 */

import { browser } from '$app/environment';
import { goto } from '$app/navigation';
import type { User } from '$lib/types/user';
import { hasRole, isAdmin, isManager, isStaff, isPortalUser } from '$lib/types/user';

let user = $state<User | null>(null);
let accessToken = $state<string | null>(null);
let accessTokenExpiration = $state<Date | null>(null);
let isLoading = $state(true);
const isAuthenticated = $derived(!!user && !!accessToken);

/** Optional hook fired when auth is cleared (e.g. to disconnect SignalR). */
let onAuthClearedCallback: (() => void) | null = null;

export function setOnAuthCleared(callback: (() => void) | null) {
	onAuthClearedCallback = callback;
}

/** Seed the store from server-provided layout data. */
export function initAuth(
	initialUser: User | null,
	initialToken: string | null,
	expiration: Date | null
) {
	user = initialUser;
	accessToken = initialToken;
	accessTokenExpiration = expiration;
	isLoading = false;
}

/** Set auth after a successful login (client-side). */
export function setAuth(newUser: User, newToken: string, expiration: Date) {
	user = newUser;
	accessToken = newToken;
	accessTokenExpiration = expiration;
	isLoading = false;
}

/** Update only the token (after a refresh). */
export function updateToken(newToken: string, expiration: Date) {
	accessToken = newToken;
	accessTokenExpiration = expiration;
}

/**
 * Clear auth state on logout / session expiry. In the browser, fires the
 * cleared callback and navigates to /login.
 */
export function clearAuth() {
	user = null;
	accessToken = null;
	accessTokenExpiration = null;
	isLoading = false;

	if (browser) {
		if (onAuthClearedCallback) {
			try {
				onAuthClearedCallback();
			} catch {
				/* best effort */
			}
		}
		void goto('/login');
	}
}

/** True if the token is expired or expires within `bufferSeconds`. */
export function isTokenExpired(bufferSeconds = 60): boolean {
	if (!accessTokenExpiration) return true;
	const buffer = bufferSeconds * 1000;
	return accessTokenExpiration.getTime() - Date.now() < buffer;
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
		}
	};
}

// ---- Convenience accessors / role helpers bound to the current user ----

export function getCurrentUser(): User | null {
	return user;
}

/** Replace the current user (e.g. after a fresh /auth/me fetch). */
export function setCurrentUser(newUser: User | null) {
	user = newUser;
	isLoading = false;
}

export function hasAnyRole(...roles: string[]): boolean {
	return hasRole(user, ...roles);
}

export function currentUserIsAdmin(): boolean {
	return isAdmin(user);
}

export function currentUserIsManager(): boolean {
	return isManager(user);
}

export function currentUserIsStaff(): boolean {
	return isStaff(user);
}

export function currentUserIsPortalUser(): boolean {
	return isPortalUser(user);
}
