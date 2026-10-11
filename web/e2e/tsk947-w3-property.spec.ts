import { test, expect } from '@playwright/test';
import { login, unique } from './helpers';

/**
 * TSK-947 W3 — "Add rental" stays on the properties list. The landlord fills the six
 * things they always know (name, street, city, state, ZIP, monthly rent) in a dialog on
 * the list; property type, owner, fees and the tax numbers wait under a collapsed
 * "More details" disclosure. The onboarding wizard is for first run only.
 */
test.describe('TSK-947 W3 — add a rental from the properties list', () => {
	test.beforeEach(async ({ page }) => {
		await login(page);
		await page.goto('/properties');
		await expect(page.getByTestId('properties-page')).toBeVisible();
		await page.waitForLoadState('networkidle');
	});

	test('adds a rental from an inline dialog without leaving the list', async ({ page }) => {
		const name = unique('Maple Street House');

		await page.getByTestId('property-create-button').click();

		// The dialog opens in place — no trip to the onboarding wizard.
		await expect(page.getByTestId('property-create-dialog')).toBeVisible();
		await expect(page).toHaveURL(/\/properties(\?|$)/);

		// One screen, not a wizard.
		await expect(page.getByTestId('property-create-step-next')).toHaveCount(0);

		// The essentials are on the face of the dialog.
		await expect(page.getByTestId('property-create-essentials')).toBeVisible();
		await expect(page.getByTestId('property-create-name-input')).toBeVisible();
		await expect(page.getByTestId('property-create-address-input')).toBeVisible();
		await expect(page.getByTestId('property-create-city-input')).toBeVisible();
		await expect(page.getByTestId('property-create-state-input')).toBeVisible();
		await expect(page.getByTestId('property-create-postal-input')).toBeVisible();
		await expect(page.getByTestId('property-create-rent-input')).toBeVisible();

		// Everything else is hidden until "More details" is opened.
		await expect(page.getByTestId('property-more-details')).toHaveCount(0);
		await expect(page.getByTestId('property-create-management-fee-input')).toHaveCount(0);
		await page.getByTestId('property-more-details-toggle').click();
		await expect(page.getByTestId('property-more-details')).toBeVisible();
		await expect(page.getByTestId('property-create-management-fee-input')).toBeVisible();
		await expect(page.getByTestId('property-create-management-fee-help')).toBeVisible();
		await expect(page.getByTestId('property-create-in-service-date-help')).toBeVisible();
		await page.getByTestId('property-more-details-toggle').click();
		await expect(page.getByTestId('property-more-details')).toHaveCount(0);

		// Happy path: the six essentials, then save.
		await page.getByTestId('property-create-name-input').fill(name);
		await page.getByTestId('property-create-address-input').fill('42 Maple Street');
		await page.getByTestId('property-create-city-input').fill('Springfield');
		// The state field is a searchable combobox: real keystrokes filter the list, then the
		// option must be clicked (keyboard Enter does not commit in this combobox).
		const stateInput = page.getByTestId('property-create-state-input');
		await stateInput.click();
		await stateInput.pressSequentially('OH');
		const ohioOption = page.getByRole('option', { name: 'Ohio' });
		await expect(ohioOption).toBeVisible();
		await ohioOption.click();
		await expect(stateInput).toHaveValue(/Ohio/);
		await expect(page.getByRole('listbox')).toHaveCount(0);
		await page.getByTestId('property-create-postal-input').fill('45503');
		await page.getByTestId('property-create-rent-input').fill('1450');
		await page.getByTestId('property-create-save').click();

		await expect(page.getByTestId('property-create-dialog')).toBeHidden();

		await page.getByTestId('property-search-input').fill(name);
		await expect(
			page.getByTestId('datagrid-desktop').getByTestId('property-name').filter({ hasText: name })
		).toBeVisible();
	});
});
