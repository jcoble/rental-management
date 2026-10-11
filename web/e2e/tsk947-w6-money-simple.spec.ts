import { expect, test } from '@playwright/test';
import { login } from './helpers';

/**
 * TSK-947 W6 — the Simple detail level keeps Money to the four tabs a landlord uses,
 * lands on Rent & payments, and keeps the bookkeeping figures out of sight. Reports lead
 * with the three everyday reports and tuck the accountant ones behind a disclosure.
 */
test.describe('TSK-947 W6 simple money', () => {
	test.use({ viewport: { width: 1710, height: 990 } });

	test('Simple hides the bookkeeping tabs and Advanced brings them back', async ({ page }) => {
		await login(page);
		await page.goto('/accounting', { waitUntil: 'domcontentloaded' });
		await expect(page.getByTestId('accounting-page')).toBeVisible();
		await page.waitForLoadState('networkidle');

		const tabs = page.getByTestId('accounting-tabs').getByRole('tab');
		await expect(tabs.first()).toBeVisible();
		expect(await tabs.count()).toBeLessThanOrEqual(4);
		await expect(page.getByTestId('accounting-tab-general-ledger')).toHaveCount(0);
		await expect(page.getByTestId('accounting-tab-rent-payments')).toHaveAttribute(
			'aria-selected',
			'true'
		);

		await page.getByTestId('accounting-tab-overview').click();
		await expect(page.getByTestId('money-summary-past-due')).toBeVisible({ timeout: 15_000 });
		await expect(page.getByTestId('money-position-facts-card')).toHaveCount(0);
		await expect(page.getByTestId('money-position-deposit-warning')).toHaveCount(0);

		await page.getByTestId('accounting-detail-mode-advanced').click();
		await expect(page.getByTestId('accounting-tab-general-ledger')).toBeVisible();
		expect(await tabs.count()).toBeGreaterThan(4);
		await page.getByTestId('accounting-tab-overview').click();
		await expect(page.getByTestId('money-position-facts-card')).toBeVisible({ timeout: 15_000 });
	});

	test('Reports lead with the everyday three and collapse the accountant reports', async ({
		page
	}) => {
		await login(page);
		await page.goto('/reports', { waitUntil: 'domcontentloaded' });
		await expect(page.getByTestId('reports-page')).toBeVisible();

		const everyday = page.getByTestId('reports-everyday');
		await expect(everyday).toBeVisible({ timeout: 15_000 });
		await expect(everyday.getByTestId('report-card-rent-roll')).toBeVisible();
		await expect(everyday.getByTestId('report-card-delinquency')).toBeVisible();
		await expect(everyday.getByTestId('report-card-income-expense-statement')).toBeVisible();
		expect(await everyday.locator('[data-testid^="report-card-"]').count()).toBe(3);

		await expect(page.getByTestId('reports-accountant')).toBeVisible();
		await expect(page.getByTestId('reports-accountant-list')).toHaveCount(0);

		await page.getByTestId('reports-accountant-toggle').click();
		await expect(page.getByTestId('reports-accountant-list')).toBeVisible();
		await expect(
			page.getByTestId('reports-accountant-list').locator('[data-testid^="report-card-"]').first()
		).toBeVisible();
	});
});
