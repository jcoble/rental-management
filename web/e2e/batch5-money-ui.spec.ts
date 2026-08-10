import { expect, test, type Locator, type Page, type Route } from '@playwright/test';
import { apiToken, findLeasedUnit, loginWithApi } from './helpers';

type Rect = { x: number; y: number; width: number; height: number };

function overlap(a: Rect, b: Rect): number {
	const width = Math.max(0, Math.min(a.x + a.width, b.x + b.width) - Math.max(a.x, b.x));
	const height = Math.max(0, Math.min(a.y + a.height, b.y + b.height) - Math.max(a.y, b.y));
	return width * height;
}

function centerDistance(a: Rect, b: Rect): number {
	return Math.abs(a.x + a.width / 2 - (b.x + b.width / 2));
}

function nearestVerticalGap(a: Rect, b: Rect): number {
	return Math.min(Math.abs(a.y - (b.y + b.height)), Math.abs(b.y - (a.y + a.height)));
}

async function visibleOverlay(page: Page): Promise<Locator> {
	const overlay = page.locator(
		'[data-slot="popover-content"]:visible, [data-slot="select-content"]:visible, [data-slot="dropdown-menu-content"]:visible, [data-bits-floating-content-wrapper] > [data-state="open"]:visible'
	).last();
	await expect(overlay).toBeVisible();
	return overlay;
}

async function fulfillJson(route: Route, body: unknown): Promise<void> {
	await route.fulfill({
		status: 200,
		contentType: 'application/json',
		body: JSON.stringify(body)
	});
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

async function assertRangeMonthDropdown(
	page: Page,
	trigger: Locator,
	side: 'left' | 'right'
): Promise<void> {
	await trigger.click();
	const content = page.locator('[data-slot="select-content"]:visible').last();
	await expect(content).toBeVisible();
	await expect(content).toHaveClass(/bg-popover/);
	await expect(content).toHaveClass(/border/);
	await expect(content).toHaveClass(/shadow-md/);
	await expect(content.locator('[data-slot="select-item"][aria-selected="true"] svg')).toHaveCount(1);

	const triggerRect = await trigger.boundingBox();
	const contentRect = await content.boundingBox();
	const viewport = page.viewportSize();
	expect(triggerRect, `${side} month trigger should have a layout rectangle`).not.toBeNull();
	expect(contentRect, `${side} month content should have a layout rectangle`).not.toBeNull();
	expect(viewport).not.toBeNull();
	const triggerBox = triggerRect!;
	const contentBox = contentRect!;
	const viewportSize = viewport!;
	expect(centerDistance(contentBox, triggerBox)).toBeLessThanOrEqual(12);
	expect(nearestVerticalGap(contentBox, triggerBox)).toBeLessThanOrEqual(8);
	expect(contentBox.x).toBeGreaterThanOrEqual(0);
	expect(contentBox.y).toBeGreaterThanOrEqual(0);
	expect(contentBox.x + contentBox.width).toBeLessThanOrEqual(viewportSize.width);
	expect(contentBox.y + contentBox.height).toBeLessThanOrEqual(viewportSize.height);
	await assertOverlayStaysInDialog(page, content);
	console.log(
		`TSK-845 RangeDatePicker ${side} month geometry: ${JSON.stringify({
			trigger: triggerBox,
			content: contentBox,
			viewport: viewportSize,
			dataSide: await content.getAttribute('data-side')
		})}`
	);

	const evidenceDir = process.env.TSK845_EVIDENCE_DIR;
	if (evidenceDir) {
		await page.screenshot({ path: `${evidenceDir}/tsk845-range-${side}-month-1710x990.png` });
	}
	await page.keyboard.press('Escape');
	await expect(content).toBeHidden();
}

async function openTenantMoney(page: Page, request: Parameters<typeof findLeasedUnit>[0]): Promise<void> {
	const token = await apiToken(request);
	const unit = await findLeasedUnit(request, token);
	await loginWithApi(page, request);
	// Enter the unit through the hydrated list so the unit page's shallow tab state is initialized
	// by SvelteKit before the money tab is selected. A direct first-load deep link races that
	// initialization and leaves the page on its loading shell.
	await page.goto('/', { waitUntil: 'networkidle' });
	await expect(page.getByTestId('dashboard-hero')).toBeVisible();
	await page.getByTestId('nav-units-picker').click();
	await page.getByTestId('command-center-all').click();
	await expect(page.getByTestId('units-page')).toBeVisible();
	await page.getByTestId('datagrid-desktop').getByTestId(`unit-row-${unit.unit.id}`).click();
	await expect(page.getByTestId('unit-page')).toBeVisible();
	await page.getByTestId('tab-money').click();
	await expect(page.getByTestId('tenant-ledger-panel')).toBeVisible({ timeout: 15_000 });
}

test.describe('Batch 5 money overlay behavior', () => {
	test.describe('desktop dialog overlays', () => {
		test.use({ viewport: { width: 1710, height: 990 } });

		test('one-time charge range calendar stays inside its dialog and reserves actions', async ({ page, request }) => {
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
			await openTenantMoney(page, request);

			await page.getByRole('button', { name: 'Record payment', exact: true }).click();
			await expect(page.getByTestId('record-payment-sheet')).toBeVisible();
			await page.locator('#record-payment-method').click();
			await assertOverlayStaysInDialog(page, await visibleOverlay(page));
			await page.getByRole('option').first().click();

			await page.locator('input[name="record-payment-apply"][value="specific"]').check();
			await page.getByTestId('record-payment-allocation').locator('[data-slot="select-trigger"]').click();
			await assertOverlayStaysInDialog(page, await visibleOverlay(page));
			await page.getByRole('button', { name: 'Cancel', exact: true }).click();
		});

		test('TSK-845 two-month RangeDatePicker opens both anchored month dropdowns', async ({ page, request }) => {
			await openTenantMoney(page, request);

			await page.getByRole('button', { name: 'Add charge', exact: true }).click();
			await expect(page.getByTestId('one-time-charge-sheet')).toBeVisible();
			await page.getByTestId('one-time-charge-service-period').click({ force: true });
			await assertOverlayStaysInDialog(page, await visibleOverlay(page));

			const monthTriggers = page.getByRole('button', { name: 'Choose month', exact: true });
			await expect(monthTriggers).toHaveCount(2);
			await assertRangeMonthDropdown(page, monthTriggers.nth(0), 'left');
			await assertRangeMonthDropdown(page, monthTriggers.nth(1), 'right');

			await page.getByRole('button', { name: 'Cancel', exact: true }).click();
		});
	});

	test('a long ordinary page select remains within the small viewport', async ({ page, request }) => {
		await page.setViewportSize({ width: 390, height: 640 });
		await loginWithApi(page, request);
		await page.goto('/accounting', { waitUntil: 'networkidle' });
		await expect(page.getByTestId('accounting-page')).toBeVisible();
		await expect(page.getByTestId('accounting-tab-overview')).toHaveAttribute('aria-selected', 'true');
		await page.getByRole('tab', { name: 'Activity', exact: true }).click();
		await expect(page.getByTestId('accounting-tab-activity')).toHaveAttribute('aria-selected', 'true');
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

test.describe('Batch 5 rendered zero-balance disclosures', () => {
	test.use({ viewport: { width: 1710, height: 990 } });

	test('General Ledger keeps returned zero accounts hidden until disclosure without refetching', async ({ page, request }) => {
		let trialBalanceRequests = 0;
		await page.route('**/api/v1/accounting/**', async (route) => {
			const path = new URL(route.request().url()).pathname;
			if (path === '/api/v1/accounting/trial-balance') {
				trialBalanceRequests += 1;
				await fulfillJson(route, {
					rows: [
						{
							accountId: 9101,
							accountCode: '1000',
							accountName: 'Operating account',
							accountType: 'Asset',
							debitBalance: 1250,
							creditBalance: 0,
							typeSubtotal: 1250,
							currency: 'USD',
							isZeroBalance: false
						},
						{
							accountId: 9102,
							accountCode: '1010',
							accountName: 'Already returned zero account',
							accountType: 'Asset',
							debitBalance: 0,
							creditBalance: 0,
							typeSubtotal: 1250,
							currency: 'USD',
							isZeroBalance: true
						}
					],
					totalDebits: 1250,
					totalCredits: 1250,
					isBalanced: true,
					// Deliberately differs from the one returned zero row: the label must use this server fact.
					zeroBalanceCount: 7
				});
				return;
			}
			if (path === '/api/v1/accounting/general-ledger') {
				await fulfillJson(route, { items: [], totalCount: 0, skip: 0, take: 25 });
				return;
			}
			await route.continue();
		});

		await loginWithApi(page, request);
		await page.goto('/accounting?tab=general-ledger');
		await expect(page.getByTestId('general-ledger-panel')).toBeVisible();
		await expect(page.getByTestId('company-balance-row-9101')).toBeVisible();
		await expect(page.getByTestId('company-zero-balance-row-9102')).toBeHidden();
		const disclosure = page.getByTestId('company-balance-zero-disclosure');
		await expect(disclosure).toContainText('Show 7 accounts with no balance');
		await expect.poll(() => trialBalanceRequests).toBeGreaterThan(0);
		const requestsBeforeToggle = trialBalanceRequests;

		await disclosure.locator('summary').click();
		await expect(page.getByTestId('company-zero-balance-row-9102')).toBeVisible();
		expect(trialBalanceRequests).toBe(requestsBeforeToggle);
	});

	test('Owner statement keeps returned zero properties hidden until disclosure without refetching', async ({ page, request }) => {
		let ownerStatementRequests = 0;
		await page.route('**/api/v1/accounting/**', async (route) => {
			const path = new URL(route.request().url()).pathname;
			if (path === '/api/v1/accounting/owner-statements') {
				await fulfillJson(route, [
					{
						ownerId: 9201,
						ownerName: 'Mixed-balance owner',
						netToOwner: 875,
						totalDistributed: 0,
						undistributed: 875
					}
				]);
				return;
			}
			if (path === '/api/v1/accounting/owner-statement') {
				ownerStatementRequests += 1;
				await fulfillJson(route, {
					ownerId: 9201,
					ownerName: 'Mixed-balance owner',
					year: 2026,
					properties: [
						{
							propertyId: 9301,
							propertyName: 'Income property',
							rentalIncome: 1000,
							expenses: 100,
							managementFee: 25,
							netToOwner: 875,
							hasBalance: true
						},
						{
							propertyId: 9302,
							propertyName: 'Already returned zero property',
							rentalIncome: 0,
							expenses: 0,
							managementFee: 0,
							netToOwner: 0,
							hasBalance: false
						}
					],
					totalIncome: 1000,
					totalExpenses: 100,
					totalManagementFee: 25,
					totalNetToOwner: 875,
					totalDistributed: 0,
					undistributed: 875,
					// Deliberately differs from the one returned zero property: the label must use this server fact.
					zeroPropertyCount: 5,
					nonZeroPropertyCount: 1
				});
				return;
			}
			await route.continue();
		});
		await page.route('**/api/v1/owner-distributions**', async (route) => fulfillJson(route, []));
		await page.route('**/api/v1/owner-contributions**', async (route) => fulfillJson(route, []));

		await loginWithApi(page, request);
		await page.goto('/owners-report');
		await expect(page.getByTestId('owners-report-page')).toBeVisible();
		await page.getByTestId('owners-report-owner-9201').click();
		await expect(page.getByTestId('owners-report-properties-table')).toBeVisible();
		await expect(page.getByTestId('owners-report-property-row-9301')).toBeVisible();
		await expect(page.getByTestId('owners-report-property-row-9302')).toHaveCount(0);
		const disclosure = page.getByTestId('owners-report-toggle-zero-properties');
		await expect(disclosure).toHaveText('Show 5 properties with no balance');
		await expect.poll(() => ownerStatementRequests).toBeGreaterThan(0);
		const requestsBeforeToggle = ownerStatementRequests;

		await disclosure.click();
		await expect(page.getByTestId('owners-report-property-row-9302')).toBeVisible();
		expect(ownerStatementRequests).toBe(requestsBeforeToggle);
	});
});
