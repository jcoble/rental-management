import { test, expect } from '@playwright/test';
import { login, unique } from './helpers';

test.describe('Properties', () => {
	test.beforeEach(async ({ page }) => {
		await login(page);
		await page.goto('/properties');
		await expect(page.getByTestId('properties-page')).toBeVisible();
		// Svelte 5 attaches delegated click handlers after the initial TanStack data-load
		// completes; wait for the network to settle so create-button clicks aren't dropped.
		await page.waitForLoadState('networkidle');
	});

	test('shows the list view with a create affordance and search', async ({ page }) => {
		await expect(page.getByTestId('property-create-button')).toBeVisible();
		await expect(page.getByTestId('property-search-input')).toBeVisible();
	});

	test('validates required fields before creating', async ({ page }) => {
		await page.getByTestId('property-create-button').click();
		await expect(page.getByTestId('property-form')).toBeVisible();
		// Submit empty -> inline Zod errors, dialog stays open.
		await page.getByTestId('property-form-save').click();
		await expect(page.getByTestId('property-name-error')).toBeVisible();
		await expect(page.getByTestId('property-form')).toBeVisible();
	});

	test('creates a property and sees it in the list', async ({ page }) => {
		const name = unique('E2E Property');
		await page.getByTestId('property-create-button').click();
		await page.getByTestId('property-name-input').fill(name);
		await page.getByTestId('property-address-input').fill('123 Test Street');
		// If a Google Places key is configured the address field shows a suggestions dropdown.
		// Dismiss it (Escape) so its overlay can't sit over the Save button below; the app also
		// guards against a late suggestion response re-opening it once focus leaves this field.
		await page.getByTestId('property-address-input').press('Escape');
		await page.getByTestId('property-city-input').fill('Austin');
		// State is now a searchable StateSelect combobox: open, filter, pick the option.
		await page.getByTestId('property-state-input').click();
		await page.getByTestId('property-state-input').fill('TX');
		await page.getByRole('option', { name: 'Texas' }).click();
		await page.getByTestId('property-zip-input').fill('78701');
		await page.getByTestId('property-form-save').click();

		// Dialog closes on success. Search to surface the new row regardless of pagination,
		// and scope to the desktop grid (the cell testid also renders in the hidden mobile card).
		await expect(page.getByTestId('property-form')).toBeHidden();
		await page.getByTestId('property-search-input').fill(name);
		await expect(
			page.getByTestId('datagrid-desktop').getByTestId('property-name').filter({ hasText: name })
		).toBeVisible();
	});
});
