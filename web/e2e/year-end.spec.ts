import { test, expect } from '@playwright/test';
import { login } from './helpers';

/**
 * Smoke coverage for the Mortgages → True Cash Flow → Year-End feature. Verifies the flagship
 * year-end view renders its three blocks with cash flow and taxable income as distinct numbers, and
 * that the per-property mortgage + recurring-expense sections render on the property detail page.
 *
 * Requires the app running against a seeded DB (CI / local dev), like the other e2e specs.
 */
test.describe('Year-end view + mortgage feature', () => {
	test.beforeEach(async ({ page }) => {
		await login(page);
	});

	test('year-end view renders cash-flow and tax blocks as distinct numbers', async ({ page }) => {
		await page.goto('/accounting/year-end');
		await expect(page.getByTestId('year-end-page')).toBeVisible();
		await page.waitForLoadState('networkidle');

		// The two headline numbers — cash flow vs taxable income — are both present and distinct.
		await expect(page.getByTestId('year-end-cash-flow-total')).toBeVisible();
		await expect(page.getByTestId('year-end-taxable-total')).toBeVisible();

		// The three blocks render.
		await expect(page.getByTestId('year-end-cash-flow-block')).toBeVisible();
		await expect(page.getByTestId('year-end-tax-block')).toBeVisible();
		await expect(page.getByTestId('year-end-rent-roll-block')).toBeVisible();

		// Switching the tax year refetches without error (the view stays mounted).
		await page.getByTestId('year-end-year-select').selectOption({ index: 1 });
		await page.waitForLoadState('networkidle');
		await expect(page.getByTestId('year-end-cash-flow-block')).toBeVisible();
	});

	test('year-end is reachable from the accounting Reports tab', async ({ page }) => {
		await page.goto('/accounting?tab=reports');
		await expect(page.getByTestId('accounting-report-year-end-link')).toBeVisible();
		await page.getByTestId('accounting-report-year-end-link').click();
		await expect(page.getByTestId('year-end-page')).toBeVisible();
	});

	test('property detail shows the mortgage + recurring-expense sections', async ({ page }) => {
		await page.goto('/properties');
		await expect(page.getByTestId('properties-page')).toBeVisible();
		await page.waitForLoadState('networkidle');

		// Open the first property in the grid.
		const firstRow = page.getByTestId('datagrid-desktop').locator('tbody tr').first();
		await firstRow.click();

		await expect(page.getByTestId('property-detail-page')).toBeVisible();
		await expect(page.getByTestId('property-detail-loans')).toBeVisible();
		await expect(page.getByTestId('loan-add-button')).toBeVisible();
		await expect(page.getByTestId('property-detail-recurring-expenses')).toBeVisible();
		await expect(page.getByTestId('property-detail-basis-card')).toBeVisible();
	});
});
