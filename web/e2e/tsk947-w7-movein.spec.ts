import { expect, test } from '@playwright/test';
import { login } from './helpers';

/**
 * TSK-947 W7 — Prepare move-in is one review screen in the landlord's words.
 * The essentials sit on a single scrollable dialog; everything else is folded
 * away under "More details" and the migration disclosure.
 */
test.describe('Prepare move-in review screen', () => {
	test('collects a move-in on one screen and saves it', async ({ page }) => {
		await login(page);

		await page.goto('/leases', { waitUntil: 'domcontentloaded' });
		await expect(page.getByTestId('leases-page')).toBeVisible();
		await page.waitForLoadState('networkidle');
		await page.getByTestId('leases-create-lease').click();

		// One review screen: no stepper, no Next/Back.
		const review = page.getByTestId('movein-review');
		await expect(review).toBeVisible();
		await expect(page.getByTestId('prepare-move-in-stepper')).toHaveCount(0);
		await expect(page.getByTestId('prepare-move-in-next')).toHaveCount(0);
		await expect(page.getByTestId('prepare-move-in-back')).toHaveCount(0);
		await expect(page.getByRole('button', { name: 'Prepare move-in' })).toBeVisible();

		// The essentials are all present without opening anything.
		await expect(page.getByTestId('prepare-move-in-manual-unit')).toBeVisible();
		await expect(page.getByTestId('prepare-move-in-term-type-input')).toBeVisible();
		await expect(page.getByTestId('prepare-move-in-term-start-input')).toBeVisible();
		await expect(page.getByTestId('prepare-move-in-rent-input')).toBeVisible();
		await expect(page.getByTestId('prepare-move-in-due-day-input')).toBeVisible();
		await expect(page.getByTestId('prepare-move-in-deposit-input')).toBeVisible();

		// The rest is hidden behind disclosures that start closed.
		const more = page.getByTestId('movein-more');
		const migration = page.getByTestId('movein-migration');
		await expect(more).toHaveJSProperty('open', false);
		await expect(migration).toHaveJSProperty('open', false);
		await expect(page.getByTestId('prepare-move-in-late-fee-input')).toBeHidden();
		await expect(page.getByTestId('prepare-move-in-opening-amount-input')).toBeHidden();
		await expect(migration).toContainText('Moving an existing lease into Rental Command?');

		// Late fee and grace live under "More details" and still need values.
		await more.getByText('More details').click();
		await expect(more).toHaveJSProperty('open', true);
		await page.getByTestId('prepare-move-in-late-fee-input').fill('0');
		await page.getByTestId('prepare-move-in-grace-input').fill('0');

		// Tenant: create a new one inline so the run does not depend on seeded tenants.
		await page.getByTestId('prepare-move-in-tenant-source').getByText('New tenant').click();
		const stamp = Date.now();
		await page.getByTestId('prepare-move-in-new-tenant-first-name').fill('Move');
		await page.getByTestId('prepare-move-in-new-tenant-last-name').fill(`In${stamp}`);
		await page.getByTestId('prepare-move-in-new-tenant-email').fill(`movein${stamp}@example.test`);

		// Unit: first vacant unit offered by the picker.
		await page.getByTestId('prepare-move-in-manual-unit-trigger').click();
		await page.getByRole('option').first().click();

		// Dates default to today; a fixed term needs an end date.
		await expect(page.getByTestId('prepare-move-in-term-start-input')).not.toHaveValue('');
		await page.getByTestId('prepare-move-in-term-end-input').fill('12/31/2027');

		await page.getByTestId('prepare-move-in-rent-input').fill('1200');
		await page.getByTestId('prepare-move-in-due-day-input').fill('1');
		await page.getByTestId('prepare-move-in-deposit-input').fill('1200');

		const prepared = page.waitForResponse(
			(response) =>
				response.url().includes('/lease-managements/prepare-move-in') && response.status() < 400
		);
		await page.getByTestId('prepare-move-in-submit').click();
		await prepared;
		await expect(page.getByText('Move-in prepared.', { exact: false })).toBeVisible();
	});
});
