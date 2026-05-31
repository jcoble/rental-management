import { test, expect } from '@playwright/test';
import { login, unique } from './helpers';

test.describe('Properties', () => {
	test.beforeEach(async ({ page }) => {
		await login(page);
		await page.goto('/properties');
		await expect(page.getByTestId('properties-page')).toBeVisible();
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
		await page.getByTestId('property-city-input').fill('Austin');
		await page.getByTestId('property-state-input').fill('TX');
		await page.getByTestId('property-zip-input').fill('78701');
		await page.getByTestId('property-form-save').click();

		// Dialog closes on success and the new row appears.
		await expect(page.getByTestId('property-form')).toBeHidden();
		await expect(page.getByTestId('property-name').filter({ hasText: name })).toBeVisible();
	});
});
