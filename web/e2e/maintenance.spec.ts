import { test, expect } from '@playwright/test';
import { login, unique } from './helpers';

test.describe('Maintenance', () => {
	test.beforeEach(async ({ page }) => {
		await login(page);
		await page.goto('/maintenance');
		await expect(page.getByTestId('maintenance-page')).toBeVisible();
		await page.waitForLoadState('networkidle');
	});

	test('creates a work order against a property', async ({ page }) => {
		const title = unique('Leaky Faucet');

		await page.getByTestId('work-order-create-button').click();
		await expect(page.getByTestId('work-order-form')).toBeVisible();

		// Pick a real property from the dropdown — the first option is the "Select property"
		// placeholder (empty value), so skip it.
		await page.getByTestId('work-order-property-input').click();
		const realProperties = page.getByRole('option').filter({ hasNotText: 'Select property' });
		const optionCount = await realProperties.count();
		test.skip(optionCount === 0, 'No properties seeded to attach a repair to');
		await realProperties.first().click();

		await page.getByTestId('work-order-title-input').fill(title);
		await page.getByTestId('work-order-description-input').fill('Kitchen sink drips overnight.');

		await page.getByTestId('work-order-form-save').click();

		// Search to surface the new work order regardless of pagination, scoped to the desktop grid.
		await expect(page.getByTestId('work-order-form')).toBeHidden();
		await page.getByTestId('work-order-search-input').fill(title);
		await expect(
			page.getByTestId('datagrid-desktop').getByTestId('work-order-title').filter({ hasText: title })
		).toBeVisible();
	});
});
