import { expect, type APIRequestContext, type Page } from '@playwright/test';

/** Seeded admin credentials (overridable via env for other environments). */
export const ADMIN_EMAIL = process.env.PW_EMAIL ?? 'admin@rentalcommand.local';
export const ADMIN_PASSWORD = process.env.PW_PASSWORD ?? 'Admin123!';

/**
 * Log in through the real login form (server action sets first-party cookies)
 * and wait for the post-login landing on the staff dashboard.
 */
export async function login(page: Page, email = ADMIN_EMAIL, password = ADMIN_PASSWORD) {
	await page.goto('/login');
	await page.getByTestId('login-email-input').fill(email);
	await page.getByTestId('login-password-input').fill(password);
	await page.getByTestId('login-submit').click();
	// Staff land on the dashboard ('/'); wait until we leave the login route.
	await expect(page).not.toHaveURL(/\/login/);
}

/** A reasonably unique suffix so repeated runs don't collide. */
export function unique(prefix: string): string {
	return `${prefix}-${Date.now().toString().slice(-6)}`;
}

/**
 * Get a bearer access token by logging in through the API (via the same-origin Vite proxy).
 * Used by the Unit Command Center functional tests to (a) discover a unit that has a current
 * lease so the Rent tab is exercisable, and (b) re-query persisted records out-of-band to prove
 * a save actually hit the database (rather than only trusting a re-render). The API authenticates
 * with `Authorization: Bearer`, so this token (not the SvelteKit httpOnly cookies) is what the
 * request context needs.
 */
export async function apiToken(
	request: APIRequestContext,
	email = ADMIN_EMAIL,
	password = ADMIN_PASSWORD
): Promise<string> {
	const res = await request.post('/api/v1/auth/login', { data: { email, password } });
	expect(res.ok(), `API login failed: ${res.status()}`).toBeTruthy();
	const body = await res.json();
	const token = body.accessToken ?? body.token;
	expect(token, 'API login returned no access token').toBeTruthy();
	return token as string;
}

/** Authorization header for an API call made with {@link apiToken}. */
export function bearer(token: string): Record<string, string> {
	return { Authorization: `Bearer ${token}` };
}

export interface UnitDashboardLite {
	unit: { id: number; propertyId: number; unitNumber: string };
	currentLease?: { id: number };
}

/**
 * Find a unit (in the caller's portfolio) that has a current lease — required for the Rent tab,
 * which only shows the post-payment affordance and payment list when a lease exists. Walks the
 * units list (newest-id first is irrelevant; the seed has many leased units) and returns the first
 * one whose dashboard carries a `currentLease.id`. Throws if none is found so the test fails loudly
 * rather than silently skipping the most important coverage.
 */
export async function findLeasedUnit(
	request: APIRequestContext,
	token: string
): Promise<UnitDashboardLite> {
	const listRes = await request.get('/api/v1/units?take=500', { headers: bearer(token) });
	expect(listRes.ok(), `units list failed: ${listRes.status()}`).toBeTruthy();
	const list = (await listRes.json()) as Array<{ id: number }>;
	for (const u of list) {
		const dRes = await request.get(`/api/v1/units/${u.id}/dashboard`, { headers: bearer(token) });
		if (!dRes.ok()) continue;
		const d = (await dRes.json()) as UnitDashboardLite;
		if (d.currentLease?.id) return d;
	}
	throw new Error('No unit with a current lease found in the seeded data');
}
