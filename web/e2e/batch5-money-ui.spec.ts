import { expect, test, type Locator, type Page } from '@playwright/test';
import { apiToken, findLeasedUnit, loginWithApi } from './helpers';

type Rect = { x: number; y: number; width: number; height: number };

function overlap(a: Rect, b: Rect): number {
	const width = Math.max(0, Math.min(a.x + a.width, b.x + b.width) - Math.max(a.x, b.x));
	const height = Math.max(0, Math.min(a.y + a.height, b.y + b.height) - Math.max(a.y, b.y));
	return width * height;
}

async function visibleOverlay(page: Page): Promise<Locator> {
	const overlay = page.locator(
		'[data-slot="popover-content"]:visible, [data-slot="select-content"]:visible, [data-slot="dropdown-menu-content"]:visible'
	).last();
	await expect(overlay).toBeVisible();
	return overlay;
}

async function assertOverlayStaysInDialog(page: Page, overlay: Locator): Promise<void> {
	const dialog = page.locator('[data-slot="dialog-content"]:visible').last();
	const dialogRect = await dialog.boundingBox();
	const overlayRect = await overlay.boundingBox();
	const footerRect = await dialog.locator('[data-slot="dialog-footer"]').boundingBox();
	expect(dialogRect, 'visible dialog should have a layout rectangle').not.toBeNull();
	expect(overlayRect, 'visible overlay should have a layout rectangle').not.toBeNull();
	expect(footerRect, 'dialog footer should have a layout rectangle').not.toBeNull();
	const dialogBox = dialogRect!;
	const overlayBox = overlayRect!;
	const footerBox = footerRect!;
	expect(overlayBox.x).toBeGreaterThanOrEqual(dialogBox.x - 1);
	expect(overlayBox.y).toBeGreaterThanOrEqual(dialogBox.y - 1);
	expect(overlayBox.x + overlayBox.width).toBeLessThanOrEqual(dialogBox.x + dialogBox.width + 1);
	expect(overlayBox.y + overlayBox.height).toBeLessThanOrEqual(dialogBox.y + dialogBox.height + 1);
	expect(overlap(overlayBox, footerBox), 'overlay must reserve the dialog footer/primary actions').toBe(0);
}

async function openTenantMoney(page: Page, request: Parameters<typeof findLeasedUnit>[0]): Promise<void> {
	const token = await apiToken(request);
	const unit = await findLeasedUnit(request, token);
	await loginWithApi(page, request);
	await page.goto(`/units/${unit.unit.id}?tab=money&view=tenant-account&tenantAccount=${unit.currentLease?.tenantAccountId ?? ''}`);
	await expect(page.getByTestId('tenant-ledger-panel')).toBeVisible({ timeout: 15_000 });
}

test.describe('Batch 5 money overlay behavior', () => {
	test('one-time charge range calendar stays inside its dialog and reserves actions', async ({ page, request }) => {
		await page.setViewportSize({ width: 390, height: 640 });
		await openTenantMoney(page, request);

		await page.getByRole('button', { name: 'Add charge', exact: true }).click();
		await expect(page.getByTestId('one-time-charge-sheet')).toBeVisible();
		await page.getByTestId('one-time-charge-service-period').click();
		const overlay = await visibleOverlay(page);
		await assertOverlayStaysInDialog(page, overlay);
		await expect(page.getByRole('button', { name: 'Custom', exact: true })).toBeVisible();
		await page.getByRole('button', { name: 'This month', exact: true }).click();
		await expect(page.getByTestId('one-time-charge-service-period')).toContainText(/\d{4}/);
		await page.getByRole('button', { name: 'Cancel', exact: true }).click();
	});

	test('payment method and open-charge selects stay inside the payment dialog', async ({ page, request }) => {
		await page.setViewportSize({ width: 390, height: 640 });
		await openTenantMoney(page, request);

		await page.getByRole('button', { name: 'Record payment', exact: true }).click();
		await expect(page.getByTestId('record-payment-sheet')).toBeVisible();
		await page.locator('#record-payment-method').click();
		await assertOverlayStaysInDialog(page, await visibleOverlay(page));
		await page.getByRole('option').first().click();

		await page.locator('input[name="record-payment-apply"][value="specific"]').check();
		await page.getByTestId('record-payment-allocation').locator('[role="combobox"]').click();
		await assertOverlayStaysInDialog(page, await visibleOverlay(page));
		await page.getByRole('button', { name: 'Cancel', exact: true }).click();
	});

	test('a long ordinary page select remains within the small viewport', async ({ page, request }) => {
		await page.setViewportSize({ width: 390, height: 640 });
		await loginWithApi(page, request);
		await page.goto('/accounting');
		await expect(page.getByTestId('accounting-page')).toBeVisible();
		await page.getByRole('tab', { name: 'Activity', exact: true }).click();
		await page.getByTestId('transaction-kind-filter').click();
		const overlay = await visibleOverlay(page);
		const overlayRect = await overlay.boundingBox();
		expect(overlayRect).not.toBeNull();
		expect(overlayRect!.x).toBeGreaterThanOrEqual(0);
		expect(overlayRect!.y).toBeGreaterThanOrEqual(0);
		expect(overlayRect!.x + overlayRect!.width).toBeLessThanOrEqual(390);
		expect(overlayRect!.y + overlayRect!.height).toBeLessThanOrEqual(640);
		await page.keyboard.press('Escape');
	});
});
