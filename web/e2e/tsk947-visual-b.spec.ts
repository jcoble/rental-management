import { expect, test } from '@playwright/test';
import { login } from './helpers';

/**
 * TSK-947 lane B — the activity history reads like a landlord's notebook (no database column
 * names, no internal record numbers), and the Rent & payments filters pick a real property and
 * unit instead of asking for numbers.
 */
test.describe('TSK-947 lane B readability', () => {
	test.use({ viewport: { width: 1710, height: 990 } });

	test('activity history keeps database wording off the page', async ({ page }) => {
		await login(page);
		await page.goto('/audit', { waitUntil: 'domcontentloaded' });
		await expect(page.getByTestId('audit-list')).toBeVisible({ timeout: 15_000 });
		await expect(page.getByTestId('audit-loading')).toHaveCount(0, { timeout: 15_000 });

		const body = (await page.locator('body').innerText()).toLowerCase();
		expect(body).toContain('activity history');
		expect(body).not.toContain('at utc');
		expect(body).not.toContain('artifact id');
		expect(body).not.toContain('user id');
	});

	test('rent & payments filters pick a property and unit by name', async ({ page }) => {
		await login(page);
		await page.goto('/accounting', { waitUntil: 'domcontentloaded' });
		const filters = page.getByTestId('portfolio-ledger-filters');
		await expect(filters).toBeVisible({ timeout: 15_000 });

		await expect(filters.locator('input[placeholder="Property ID"]')).toHaveCount(0);
		await expect(filters.locator('input[placeholder="Unit ID"]')).toHaveCount(0);
		await expect(filters.getByTestId('portfolio-ledger-property')).toBeVisible();
		await expect(filters.getByTestId('portfolio-ledger-property')).toContainText('Property');
		await expect(filters.getByTestId('portfolio-ledger-unit')).toContainText('Unit');
	});
});
