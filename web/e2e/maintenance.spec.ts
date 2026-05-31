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

		// Pick the first real property option (index 0 is the placeholder).
		const propertySelect = page.getByTestId('work-order-property-input');
		const optionValues = await propertySelect.locator('option').evaluateAll((opts) =>
			(opts as HTMLOptionElement[]).map((o) => o.value).filter((v) => v !== '')
		);
		test.skip(optionValues.length === 0, 'No properties seeded to attach a work order to');
		await propertySelect.selectOption(optionValues[0]);

		await page.getByTestId('work-order-title-input').fill(title);
		await page.getByTestId('work-order-description-input').fill('Kitchen sink drips overnight.');
		await page.getByTestId('work-order-form-save').click();

		await expect(page.getByTestId('work-order-form')).toBeHidden();
		await expect(page.getByTestId('work-order-title').filter({ hasText: title })).toBeVisible();
	});
});
