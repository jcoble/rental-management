import { test, expect } from '@playwright/test';
import { login, ADMIN_EMAIL } from './helpers';

test.describe('Authentication', () => {
	test('unauthenticated visit to a protected route redirects to login', async ({ page }) => {
		await page.goto('/properties');
		await expect(page).toHaveURL(/\/login/);
		await expect(page.getByTestId('login-form')).toBeVisible();
	});

	test('admin can sign in and reach the app shell', async ({ page }) => {
		await login(page);
		// After login we should be in the authenticated staff app, not on /login.
		await expect(page).not.toHaveURL(/\/login/);
		// The properties nav entry is only rendered for authenticated staff.
		await page.goto('/properties');
		await expect(page.getByTestId('properties-page')).toBeVisible();
	});

	test('invalid credentials show an inline error', async ({ page }) => {
		await page.goto('/login');
		await page.getByTestId('login-email-input').fill(ADMIN_EMAIL);
		await page.getByTestId('login-password-input').fill('wrong-password');
		await page.getByTestId('login-submit').click();
		await expect(page.getByTestId('login-error')).toBeVisible();
		await expect(page).toHaveURL(/\/login/);
	});
});
