import type { AuthUser, UserRole } from '$lib/types';
import { authToken } from '$lib/api/client';

let _user = $state<AuthUser | null>(getInitialUser());

function getInitialUser(): AuthUser | null {
	if (typeof window === 'undefined') return null;
	const raw = localStorage.getItem('rental:authUser');
	if (!raw) return null;
	try {
		return JSON.parse(raw) as AuthUser;
	} catch {
		return null;
	}
}

export function getCurrentUser(): AuthUser | null {
	return _user;
}

export function setCurrentUser(user: AuthUser | null) {
	_user = user;
	if (typeof window !== 'undefined') {
		if (user) localStorage.setItem('rental:authUser', JSON.stringify(user));
		else localStorage.removeItem('rental:authUser');
	}
}

export function isAuthenticated(): boolean {
	return !!authToken.get() && !!_user;
}

export function hasAnyRole(...roles: UserRole[]): boolean {
	if (!_user) return false;
	return roles.includes(_user.role);
}

export function clearAuth() {
	authToken.clear();
	setCurrentUser(null);
}
