import { expect, type Page } from '@playwright/test';

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
