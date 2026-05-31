import { test, expect } from '@playwright/test';
import path from 'path';
import { login } from './helpers';

const RECEIPT_PATH = path.join(__dirname, 'fixtures', 'receipt.png');

test.describe('Scan intake', () => {
	/**
	 * Journey 1: upload → redirect to review.
	 * Drops a receipt onto the upload page, waits for the background extraction (no-op
	 * in dev/test with no API key), and asserts the review form is rendered.
	 * Does NOT assert specific extracted values — they will be empty/zero in no-op mode.
	 */
	test('upload receipt redirects to scan review page', async ({ page }) => {
		await login(page);
		await page.goto('/scan');

		// Locate the file-drop input and upload the fixture.
		const fileInput = page.getByTestId('file-drop-input');
		await fileInput.setInputFiles(RECEIPT_PATH);

		// After upload the app navigates to /scan/<id> with the review form.
		await expect(page).toHaveURL(/\/scan\/\d+/, { timeout: 15_000 });
		await expect(page.getByTestId('scan-review')).toBeVisible({ timeout: 15_000 });
	});

	/**
	 * Journey 2: fill review fields → confirm → success.
	 * Uploads a receipt, fills in the editable fields (override whatever was extracted),
	 * confirms, and checks the success toast and navigation to /accounting.
	 * Resilient to no-op extraction values.
	 */
	test('fill scan fields and confirm creates an expense', async ({ page }) => {
		await login(page);
		await page.goto('/scan');

		const fileInput = page.getByTestId('file-drop-input');
		await fileInput.setInputFiles(RECEIPT_PATH);

		await expect(page).toHaveURL(/\/scan\/\d+/, { timeout: 15_000 });
		await expect(page.getByTestId('scan-review')).toBeVisible({ timeout: 15_000 });

		// Override fields to ensure the form can be submitted regardless of extraction.
		const vendorField = page.getByTestId('scan-field-vendor_name');
		if (await vendorField.isVisible()) {
			await vendorField.fill('Test Vendor');
		}

		const amountField = page.getByTestId('scan-field-amount');
		if (await amountField.isVisible()) {
			await amountField.fill('25.00');
		}

		const dateField = page.getByTestId('scan-field-transaction_date');
		if (await dateField.isVisible()) {
			await dateField.fill('2026-01-15');
		}

		// Click confirm.
		await page.getByTestId('scan-confirm').click();

		// Expect a success toast and navigation to /accounting.
		await expect(page.getByTestId('scan-success-toast')).toBeVisible({ timeout: 15_000 });
		await expect(page).toHaveURL(/\/accounting/, { timeout: 15_000 });
	});
});
