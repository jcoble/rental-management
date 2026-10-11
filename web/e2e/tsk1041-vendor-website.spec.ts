import { test, expect } from '@playwright/test';
import { login, unique } from './helpers';

/**
 * TSK-1041 — the vendor's website is saved by the form but never shown on the
 * vendor's detail card. The landlord should be able to open the vendor and
 * click straight through to their website.
 */
test.describe('TSK-1041 — vendor website on the detail card', () => {
	test('shows the website as a link', async ({ page }) => {
		const website = `https://${unique('acme-plumbing').toLowerCase().replace(/[^a-z0-9]+/g, '-')}.example.com`;

		await login(page);
		await page.goto('/vendors');
		await expect(page.getByTestId('vendors-page')).toBeVisible();
		await page.waitForLoadState('networkidle');

		// Give the first vendor a website through the edit dialog.
		await page.getByTestId('vendor-edit').first().click();
		await expect(page.getByTestId('vendor-form-cancel')).toBeVisible();
		while ((await page.getByTestId('vendor-website-input').count()) === 0) {
			await page.getByTestId('vendor-step-next').click();
		}
		await page.getByTestId('vendor-website-input').fill(website);
		while ((await page.getByTestId('vendor-form-save').count()) === 0) {
			await page.getByTestId('vendor-step-next').click();
		}
		await page.getByTestId('vendor-form-save').click();
		await expect(page.getByTestId('vendor-form-save')).toHaveCount(0);

		// Open that vendor and check the website is on the card, as a link.
		await page.getByTestId('vendor-row').first().click();
		await expect(page.getByTestId('vendor-detail-card')).toBeVisible();

		const link = page.getByTestId('vendor-detail-website').getByRole('link');
		await expect(link).toBeVisible();
		await expect(link).toHaveAttribute('href', website);
		await expect(link).toHaveText(website.replace(/^https?:\/\//, ''));
	});
});
