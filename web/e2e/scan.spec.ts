import { test, expect } from '@playwright/test';
import path from 'path';
import { fileURLToPath } from 'node:url';
import { login } from './helpers';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const RECEIPT_PATH = path.join(HERE, 'fixtures', 'receipt.png');

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
		// Svelte 5 delegates change; setInputFiles does not trip it. Settle first to avoid a
		// double-fire race with Playwright's own post-setInputFiles event, then dispatch once.
		await page.waitForTimeout(400);
		await fileInput.dispatchEvent('change');

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
		// Svelte 5 delegates change; setInputFiles does not trip it. Settle first to avoid a
		// double-fire race with Playwright's own post-setInputFiles event, then dispatch once.
		await page.waitForTimeout(400);
		await fileInput.dispatchEvent('change');

		await expect(page).toHaveURL(/\/scan\/\d+/, { timeout: 15_000 });
		await expect(page.getByTestId('scan-review')).toBeVisible({ timeout: 15_000 });

		// This journey requires a working LLM key: without one the OpenAI provider runs in
		// no-op mode, the worker marks the draft "Failed" (no fields extracted), and the API
		// refuses to confirm any draft that isn't in "Reviewing" — so confirm can never succeed.
		// Skip (rather than fail) when extraction is unavailable so a keyless local/CI run stays
		// green. To exercise the full path, set the API user-secret:
		//   dotnet user-secrets set "Assistant:ApiKey" "sk-…" --project RentalCommand.Api
		await page.waitForLoadState('networkidle');
		if (await page.getByTestId('scan-failed-banner').isVisible()) {
			test.skip(true, 'LLM extraction unavailable (no Assistant:ApiKey) — draft Failed, cannot confirm.');
		}

		// Override fields to ensure the form can be submitted regardless of extraction.
		const vendorField = page.getByTestId('scan-field-vendor_name');
		if (await vendorField.isVisible()) {
			await vendorField.fill('Test Vendor');
		}

		// The amount the expense uses comes from the "total" field (resolvedAmount reads
		// editedFields['total']); its testid is scan-field-total, not scan-field-amount.
		const amountField = page.getByTestId('scan-field-total');
		if (await amountField.isVisible()) {
			await amountField.fill('25.00');
		}

		const dateField = page.getByTestId('scan-field-transaction_date');
		if (await dateField.isVisible()) {
			await dateField.fill('2026-01-15');
		}

		// Click confirm.
		await page.getByTestId('scan-confirm').click();

		// After confirm the page keeps context: a success card with a link to the
		// created record (the app intentionally no longer dumps you onto /accounting).
		await expect(page.getByTestId('scan-confirm-success')).toBeVisible({ timeout: 15_000 });
		await expect(page.getByTestId('scan-view-record')).toBeVisible();
	});
});
