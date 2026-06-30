/**
 * Unit Command Center smoke — run against the LIVE local dev stack (seeded data).
 *
 * Covers spec section 13 acceptance criteria #1 at the UI level: the /units list renders,
 * opening a unit shows the header (health chips), the derived clickable lifecycle stepper,
 * the unit work tabs (and a tab switch works), the activity flyout, and that
 * domain work is INLINE in the tab (an inline create form reveals — no drawer/modal).
 *
 * NOTE: this file was authored by the build agent but NOT executed — running it needs the
 * live API + web + a seeded DB, which were reserved during authoring. Run with:
 *   pnpm -C web test:e2e -- units.spec.ts
 * (requires the dev stack up at https://localhost:5667 and the seeded admin account).
 */

import { test, expect } from '@playwright/test';
import { login } from './helpers';

test.describe('Unit Command Center', () => {
	test('units list renders and links to a unit page', async ({ page }) => {
		await login(page);
		await page.goto('/units');
		await expect(page.getByTestId('units-page')).toBeVisible();

		// The list renders rows from GET /units/list-with-health.
		const firstRow = page.locator('[data-testid^="unit-row-"]').first();
		await expect(firstRow).toBeVisible({ timeout: 15_000 });

		// Opening a row navigates to the unit Command Center.
		await firstRow.click();
		await expect(page).toHaveURL(/\/units\/\d+/);
		await expect(page.getByTestId('unit-page')).toBeVisible({ timeout: 15_000 });
	});

	test('unit page shows header, lifecycle rail, tabs, and activity flyout', async ({ page }) => {
		await login(page);

		// Enter a unit via the list (avoids hard-coding an id).
		await page.goto('/units');
		const firstRow = page.locator('[data-testid^="unit-row-"]').first();
		await expect(firstRow).toBeVisible({ timeout: 15_000 });
		await firstRow.click();
		await expect(page.getByTestId('unit-page')).toBeVisible({ timeout: 15_000 });

		// Header (health summary) + title.
		await expect(page.getByTestId('unit-header')).toBeVisible();
		await expect(page.getByTestId('unit-title')).toBeVisible();
		await expect(page.getByTestId('unit-health-chips')).toBeVisible();

		// Derived lifecycle rail with a highlighted current stage.
		const rail = page.getByTestId('lifecycle-rail');
		await expect(rail).toBeVisible();
		await expect(rail).toHaveAttribute('data-stage', /\w+/);

		// The unit work tabs render; the Overview tab is the default.
		await expect(page.getByTestId('unit-tabs')).toBeVisible();
		await expect(page.getByTestId('unit-overview-tab')).toBeVisible();

		// Recent activity is available from the compact flyout, keeping the main tab layout wide.
		await expect(page.getByTestId('unit-activity-open')).toBeVisible();
		await page.getByTestId('unit-activity-open').click();
		await expect(page.getByTestId('unit-activity-flyout')).toBeVisible();
		await expect(page.getByTestId('unit-timeline-rail')).toBeVisible();
		await page.getByTestId('unit-activity-close').click();
		await expect(page.getByTestId('unit-activity-flyout')).toBeHidden();

		// Switching tabs swaps the panel (Overview → Ledger → tenant account).
		await page.getByTestId('tab-ledger').click();
		await expect(page.getByTestId('unit-ledger-tab')).toBeVisible({ timeout: 10_000 });
		await expect(page.getByTestId('unit-rent-tab')).toBeVisible({ timeout: 10_000 });
		await expect(page).toHaveURL(/tab=ledger/);
	});

	test('deep link with ?tab= opens the requested tab', async ({ page }) => {
		await login(page);

		// Get a real unit id from the list, then deep-link to its maintenance tab.
		await page.goto('/units');
		const firstRow = page.locator('[data-testid^="unit-row-"]').first();
		await expect(firstRow).toBeVisible({ timeout: 15_000 });
		const testId = await firstRow.getAttribute('data-testid'); // unit-row-<id>
		const unitId = testId?.replace('unit-row-', '');
		expect(unitId).toBeTruthy();

		await page.goto(`/units/${unitId}?tab=maintenance`);
		await expect(page.getByTestId('unit-maintenance-tab')).toBeVisible({ timeout: 15_000 });
	});

	test('inline work: stepper navigates and the create form is inline (no drawer)', async ({ page }) => {
		await login(page);
		await page.goto('/units');
		const firstRow = page.locator('[data-testid^="unit-row-"]').first();
		await expect(firstRow).toBeVisible({ timeout: 15_000 });
		await firstRow.click();
		await expect(page.getByTestId('unit-page')).toBeVisible({ timeout: 15_000 });

		// The lifecycle stepper is clickable: clicking the Active stage jumps to the Ledger tab.
		await page.getByTestId('lifecycle-stage-Active').click();
		await expect(page.getByTestId('unit-ledger-tab')).toBeVisible({ timeout: 10_000 });
		await expect(page.getByTestId('unit-rent-tab')).toBeVisible({ timeout: 10_000 });
		await expect(page).toHaveURL(/tab=ledger/);

		// Open the operating-costs ledger and reveal the INLINE add-expense form on the page (not a drawer/modal).
		await page.getByTestId('ledger-tab-expenses').click();
		await expect(page.getByTestId('unit-expenses-tab')).toBeVisible({ timeout: 10_000 });
		await page.getByTestId('expenses-create').click();
		await expect(page.getByTestId('expenses-create-form')).toBeVisible();
		await expect(page.getByTestId('expenses-description-input')).toBeVisible();
	});
});
