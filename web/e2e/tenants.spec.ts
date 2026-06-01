import { test, expect } from '@playwright/test';
import { login, unique } from './helpers';

test.describe('Tenants', () => {
	test.beforeEach(async ({ page }) => {
		await login(page);
		await page.goto('/tenants');
		await expect(page.getByTestId('tenants-page')).toBeVisible();
		await page.waitForLoadState('networkidle');
	});

	test('creates then edits a tenant', async ({ page }) => {
		const last = unique('Renter');

		// Create
		await page.getByTestId('tenant-create-button').click();
		await expect(page.getByTestId('tenant-form')).toBeVisible();
		await page.getByTestId('tenant-first-name-input').fill('Pat');
		await page.getByTestId('tenant-last-name-input').fill(last);
		await page.getByTestId('tenant-form-save').click();
		await expect(page.getByTestId('tenant-form')).toBeHidden();

		// Search to surface the new tenant on page 1 regardless of pagination, and scope to
		// the desktop grid (the row testid also renders in the CSS-hidden mobile card).
		await page.getByTestId('tenant-search-input').fill(last);
		const desktop = page.getByTestId('datagrid-desktop');
		const row = desktop.getByTestId('tenant-row').filter({ hasText: last });
		await expect(row).toBeVisible();

		// Edit the same tenant's first name.
		await row.getByTestId('tenant-edit').click();
		await expect(page.getByTestId('tenant-form')).toBeVisible();
		await page.getByTestId('tenant-first-name-input').fill('Patricia');
		await page.getByTestId('tenant-form-save').click();
		await expect(page.getByTestId('tenant-form')).toBeHidden();

		await expect(
			desktop.getByTestId('tenant-name').filter({ hasText: 'Patricia' }).filter({ hasText: last })
		).toBeVisible();
	});
});
