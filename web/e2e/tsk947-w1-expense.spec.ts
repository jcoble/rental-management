import { test, expect } from '@playwright/test';
import { login, unique } from './helpers';

/**
 * TSK-947 W1 — recording an expense is one screen, not a seven-step wizard.
 * The five essentials are on view straight away; everything else stays folded into "More details".
 */
test.describe('One-screen expense entry', () => {
	test.describe.configure({ timeout: 90_000 });

	test('shows five essentials, keeps the rest folded away, and saves', async ({ page }) => {
		const description = `${unique('TSK947')} ladder`;

		await login(page);
		await page.goto('/accounting?tab=activity', { waitUntil: 'domcontentloaded' });
		await expect(page.getByTestId('accounting-page')).toBeVisible();

		await page.getByTestId('expense-create-button').click();

		const essentials = page.getByTestId('expense-essentials');
		await expect(essentials).toBeVisible();
		await expect(page.getByTestId('expense-description-input')).toBeVisible();
		await expect(page.getByTestId('expense-amount-input')).toBeVisible();
		await expect(page.getByTestId('expense-incurred-input')).toBeVisible();
		await expect(page.getByTestId('expense-property-input')).toBeVisible();
		await expect(page.getByTestId('expense-category-input')).toBeVisible();

		// Scanning a receipt is offered right at the top as the faster way in.
		await expect(page.getByTestId('expense-scan-link')).toHaveAttribute('href', '/scan');

		// Everything beyond the essentials is folded away until asked for.
		const moreDetails = page.getByTestId('expense-more-details');
		await expect(moreDetails).not.toHaveAttribute('open', /.*/);
		await expect(page.getByTestId('expense-notes-input')).toBeHidden();
		await moreDetails.getByTestId('expense-more-details-summary').click();
		await expect(moreDetails).toHaveAttribute('open', /.*/);
		await expect(page.getByTestId('expense-notes-input')).toBeVisible();
		await moreDetails.getByTestId('expense-more-details-summary').click();
		await expect(page.getByTestId('expense-notes-input')).toBeHidden();

		// The date defaults to today, so a plain expense needs only two answers.
		await expect(page.getByTestId('expense-incurred-input')).not.toHaveValue('');
		await page.getByTestId('expense-description-input').fill(description);
		await page.getByTestId('expense-amount-input').fill('64.25');
		await page.getByTestId('expense-form-save').click();

		await expect(page.getByTestId('expense-essentials')).toBeHidden();

		await page.getByTestId('transaction-search').fill(description);
		await expect(page.getByTestId('transactions-list').getByText(description)).toBeVisible();
	});
});
