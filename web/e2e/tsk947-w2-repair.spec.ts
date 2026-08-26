import { test, expect } from '@playwright/test';
import { login, unique } from './helpers';

/**
 * TSK-947 W2 — "Report a repair" is one screen. The landlord sees only which rental,
 * what's wrong and the details; priority, scheduling, people and cost wait under a
 * collapsed "More details" disclosure. After saving there is a way straight to the
 * repair to set a date or pick who will fix it.
 */
test.describe('TSK-947 W2 — report a repair on one screen', () => {
	test.beforeEach(async ({ page }) => {
		await login(page);
		await page.goto('/maintenance');
		await expect(page.getByTestId('maintenance-page')).toBeVisible();
		await page.waitForLoadState('networkidle');
	});

	test('saves a repair from one screen and offers to schedule or assign it', async ({ page }) => {
		const title = unique('Bathroom fan rattling');

		await page.getByTestId('work-order-create-button').click();
		await expect(page.getByTestId('work-order-form')).toBeVisible();

		// One screen, not a wizard.
		await expect(page.getByTestId('work-order-step-next')).toHaveCount(0);
		await expect(page.getByTestId('work-order-form-save')).toBeVisible();

		// The essentials are on the face of the dialog.
		const essentials = page.getByTestId('repair-essentials');
		await expect(essentials).toBeVisible();
		await expect(page.getByTestId('work-order-property-input')).toBeVisible();
		await expect(page.getByTestId('work-order-unit-input')).toBeVisible();
		await expect(page.getByTestId('work-order-title-input')).toBeVisible();
		await expect(page.getByTestId('work-order-description-input')).toBeVisible();

		// Everything else is hidden until "More details" is opened.
		await expect(page.getByTestId('repair-more-details')).toHaveCount(0);
		await expect(page.getByTestId('work-order-estimated-cost-input')).toHaveCount(0);
		await page.getByTestId('repair-more-details-toggle').click();
		await expect(page.getByTestId('repair-more-details')).toBeVisible();
		await expect(page.getByTestId('work-order-priority-input')).toBeVisible();
		await expect(page.getByTestId('work-order-vendor-input')).toBeVisible();
		await expect(page.getByTestId('work-order-estimated-cost-input')).toBeVisible();
		await page.getByTestId('repair-more-details-toggle').click();
		await expect(page.getByTestId('repair-more-details')).toHaveCount(0);

		// Happy path against sample data: pick the first real property, describe the problem, save.
		await page.getByTestId('work-order-property-input').click();
		const realProperties = page.getByRole('option').filter({ hasNotText: 'Select property' });
		const optionCount = await realProperties.count();
		test.skip(optionCount === 0, 'No properties seeded to attach a repair to');
		await realProperties.first().click();

		await page.getByTestId('work-order-title-input').fill(title);
		await page
			.getByTestId('work-order-description-input')
			.fill('The extractor fan rattles whenever it runs.');
		await page.getByTestId('work-order-form-save').click();

		await expect(page.getByTestId('work-order-form')).toBeHidden();
		await expect(page.getByTestId('repair-schedule-link')).toBeVisible();
		await expect(page.getByTestId('repair-schedule-link')).toContainText(
			'Schedule or assign someone'
		);

		await page.getByTestId('work-order-search-input').fill(title);
		await expect(
			page.getByTestId('datagrid-desktop').getByTestId('work-order-title').filter({ hasText: title })
		).toBeVisible();
	});
});
