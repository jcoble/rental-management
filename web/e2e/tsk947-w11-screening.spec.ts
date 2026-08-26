import { expect, test } from '@playwright/test';
import { login } from './helpers';

/**
 * TSK-947 W11 — one screening action instead of an integration choice.
 * The landlord sees a single button. With no screening service connected it
 * reads "Record a screening done elsewhere" and opens a compact form whose
 * report details stay folded away until a decline leans on the report.
 */
test.describe('One screening action', () => {
	test('offers one button and opens report details for a report-based decline', async ({ page }) => {
		await login(page);

		// Find an open application whose applicant gave consent — screening needs both.
		await page.goto('/applications', { waitUntil: 'domcontentloaded' });
		await expect(page.getByTestId('applications-page')).toBeVisible();
		await page.waitForLoadState('networkidle');
		await page.getByTestId('application-status-filter').click();
		await page.getByRole('option', { name: 'Submitted', exact: true }).click();

		const rows = page.getByTestId('application-row');
		test.skip((await rows.count()) === 0, 'No submitted application exists in this sample data.');
		await expect(rows.first()).toBeVisible({ timeout: 15_000 });

		const requestScreening = page.getByTestId('application-request-screening');
		const rowCount = Math.min(await rows.count(), 5);
		let found = false;
		for (let index = 0; index < rowCount; index += 1) {
			await page.goto('/applications', { waitUntil: 'domcontentloaded' });
			await page.waitForLoadState('networkidle');
			await page.getByTestId('application-status-filter').click();
			await page.getByRole('option', { name: 'Submitted', exact: true }).click();
			await rows.nth(index).click();
			await expect(page.getByTestId('application-screening-card')).toBeVisible({ timeout: 15_000 });
			if (await requestScreening.isEnabled()) {
				found = true;
				break;
			}
		}
		test.skip(!found, 'No seeded open application with applicant consent.');

		// Exactly one screening button, and no integration jargon anywhere on the card.
		const card = page.getByTestId('application-screening-card');
		await expect(requestScreening).toHaveCount(1);
		await expect(page.getByTestId('screening-mode-picker')).toHaveCount(0);
		await expect(card).not.toContainText('Integrated');
		await expect(card).not.toContainText('External');

		// A connected service would invite the applicant instead of opening the form.
		const label = (await requestScreening.innerText()).trim();
		test.skip(label !== 'Record a screening done elsewhere', 'A screening service is connected here.');

		await requestScreening.click();
		await expect(page.getByTestId('screening-elsewhere-form')).toBeVisible();

		// Essentials are on the one screen; the rest is folded away.
		await expect(page.getByTestId('screening-provider-input')).toBeVisible();
		await expect(page.getByTestId('screening-result-select')).toBeVisible();
		await expect(page.getByTestId('screening-link-input')).toBeVisible();
		const details = page.getByTestId('screening-report-details');
		await expect(details).toHaveJSProperty('open', false);
		await expect(page.getByTestId('screening-report-company-name-input')).toBeHidden();

		// Decline plus "the report influenced this" opens the details and explains why.
		await page.getByTestId('screening-result-select').click();
		await page.getByRole('option', { name: 'Decline', exact: true }).click();
		await expect(details).toHaveJSProperty('open', false);
		await page.getByTestId('screening-report-used').click();
		await expect(details).toHaveJSProperty('open', true);
		await expect(page.getByTestId('screening-report-details-note')).toContainText(
			'Required before declining based on this report.'
		);

		// Happy path: record a plain in-progress screening instead.
		await page.getByTestId('screening-report-used').click();
		await page.getByTestId('screening-result-select').click();
		await page.getByRole('option', { name: 'Still in progress', exact: true }).click();
		await page.getByTestId('screening-provider-input').fill('Zillow');

		const saved = page.waitForResponse(
			(response) => response.url().includes('/screening/external') && response.status() < 400
		);
		await page.getByTestId('screening-elsewhere-save').click();
		await saved;
		await expect(page.getByText('Screening saved.', { exact: false })).toBeVisible();
		await expect(page.getByTestId('application-screening-result')).toBeVisible();
	});
});
