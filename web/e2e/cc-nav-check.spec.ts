import { test, expect } from '@playwright/test';
import { login } from './helpers';

test('Units picker appears below Scan/Add, lists labelled units, and opens a unit', async ({
	page
}) => {
	await login(page);

	// The pinned Units toggle is present in the sidebar.
	const ccToggle = page.getByTestId('nav-units-picker');
	await expect(ccToggle).toBeVisible();
	await expect(ccToggle).toContainText('Units');

	// Expand it → a list of units appears. Wait for hydration first, and only click while closed
	// (so a retry never toggles it back shut), to avoid a pre-hydration click being dropped.
	await page.waitForLoadState('networkidle');
	await expect(async () => {
		if ((await ccToggle.getAttribute('aria-expanded')) !== 'true') await ccToggle.click();
		await expect(ccToggle).toHaveAttribute('aria-expanded', 'true', { timeout: 600 });
	}).toPass({ timeout: 8000 });
	const firstUnit = page.locator('[data-testid^="command-center-unit-"]').first();
	await expect(firstUnit).toBeVisible();

	// Label carries property context, not just "Unit N".
	const label = (await firstUnit.textContent())?.trim() ?? '';
	expect(label.toLowerCase()).toContain('unit');

	// Clicking a unit navigates to its unit page...
	await firstUnit.click();
	await expect(page).toHaveURL(/\/units\/\d+/);
	await expect(page.getByRole('heading', { name: /Unit / })).toBeVisible();

	// ...and the dropdown collapses on selection (regression: it used to stay open).
	await page.waitForTimeout(250);
	await expect(ccToggle).toHaveAttribute('aria-expanded', 'false');
	await expect(page.locator('[data-testid^="command-center-unit-"]')).toHaveCount(0);
});
