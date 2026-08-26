import { test, expect } from '@playwright/test';
import { login, unique } from './helpers';

test.describe('New features (message center, owners report, owner email)', () => {
	test.beforeEach(async ({ page }) => {
		await login(page);
	});

	test('message center inbox renders', async ({ page }) => {
		await page.goto('/messages');
		await expect(page.getByTestId('messages-page')).toBeVisible();
		await page.waitForLoadState('networkidle');
		await expect(page.getByTestId('conversation-list')).toBeVisible();
	});

	test('owners report page renders', async ({ page }) => {
		await page.goto('/owners-report');
		await expect(page.getByTestId('owners-report-page')).toBeVisible();
	});

	test('create an owner with an email address', async ({ page }) => {
		await page.goto('/owners');
		await expect(page.getByTestId('owners-page')).toBeVisible();
		await page.waitForLoadState('networkidle');

		const name = unique('E2E Owner');
		await page.getByTestId('owner-create-button').click();
		await expect(page.getByTestId('owner-form')).toBeVisible();
		await page.getByTestId('owner-name-input').fill(name);
		await page.getByTestId('owner-step-next').click();
		await page.getByTestId('owner-step-next').click();
		await page.getByTestId('owner-email-input').fill('e2e-owner@example.com');
		await page.getByTestId('owner-form-save').click();

		// Dialog closes on success. The grid is sorted oldest-first and paginates at 20 rows,
		// so a freshly created owner (newest) can land on a later page once owners accumulate
		// across repeated runs — search to surface the new row regardless of pagination, and
		// scope to the desktop grid (the cell testid also renders in the hidden mobile card).
		await expect(page.getByTestId('owner-form')).toBeHidden();
		await page.getByTestId('owner-search-input').fill(name);
		await expect(
			page.getByTestId('datagrid-desktop').getByTestId('owner-name').filter({ hasText: name })
		).toBeVisible();
	});
});
