import { test, expect } from '@playwright/test';
import { login, unique } from './helpers';

test.describe('Maintenance', () => {
	test.beforeEach(async ({ page }) => {
		await login(page);
		await page.goto('/maintenance');
		await expect(page.getByTestId('maintenance-page')).toBeVisible();
	});

	test('creates a work order against a property', async ({ page }) => {
		const title = unique('Leaky Faucet');

		await page.getByTestId('work-order-create-button').click();
		await expect(page.getByTestId('work-order-form')).toBeVisible();

		// Pick the first real property from the shadcn Select dropdown.
		await page.getByTestId('work-order-property-input').click();
		const propertyOptions = page.getByRole('option');
		const optionCount = await propertyOptions.count();
		test.skip(optionCount === 0, 'No properties seeded to attach a work order to');
		await propertyOptions.first().click();

		await page.getByTestId('work-order-title-input').fill(title);
		await page.getByTestId('work-order-description-input').fill('Kitchen sink drips overnight.');
		await page.getByTestId('work-order-form-save').click();

		await expect(page.getByTestId('work-order-form')).toBeHidden();
		await expect(page.getByTestId('work-order-title').filter({ hasText: title })).toBeVisible();
	});
});
