import { test, expect } from '@playwright/test';
import { login, unique } from './helpers';

test.describe('Properties', () => {
	test.beforeEach(async ({ page }) => {
		// The address field uses Google Places autocomplete. When a Places key is configured
		// (it is in this dev/CI env), typing a street pops an async suggestions dropdown whose
		// overlay — and its late, debounced response that can re-open it after focus moves on —
		// keeps the dialog re-laying-out, which makes later clicks (state combobox, Save) flaky
		// with "element is not stable / detached". Neutralize Places at the network boundary so
		// the field self-disables autocomplete (no dropdown) and the form is deterministic. The
		// create flow still types a real address into the input; only the suggestions are off.
		await page.route('**/api/v1/places/**', (route) =>
			route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify({ enabled: false, suggestions: [] })
			})
		);
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
		await page.getByTestId('property-city-input').fill('Austin');
		// State is a searchable StateSelect combobox that live-filters its option list on every
		// keystroke. Two races to avoid: (1) a programmatic .fill() doesn't drive the combobox's
		// controlled input cleanly and the list churns; (2) keyboard commit (ArrowDown/Enter)
		// does NOT select in this bits-ui combobox (verified: Enter leaves it open, value empty).
		// Robust path: type real keystrokes (pressSequentially) so the list filters down to the
		// single "Texas (TX)" match, wait for that option, then click it — which commits cleanly.
		const stateInput = page.getByTestId('property-state-input');
		await expect(stateInput).toBeVisible();
		await stateInput.click();
		await stateInput.pressSequentially('TX');
		const texasOption = page.getByRole('option', { name: 'Texas' });
		await expect(texasOption).toBeVisible();
		await texasOption.click();
		// Combobox closes and shows the committed label once the pick lands.
		await expect(stateInput).toHaveValue(/Texas/);
		// The combobox's floating listbox unmounts after selection; wait for it to leave the DOM so
		// the dialog footer has settled before submitting.
		await expect(page.getByRole('listbox')).toHaveCount(0);
		await page.getByTestId('property-zip-input').fill('78701');
		const saveButton = page.getByTestId('property-form-save');
		await expect(saveButton).toBeEnabled();
		await saveButton.click();

		// Dialog closes on success. Search to surface the new row regardless of pagination,
		// and scope to the desktop grid (the cell testid also renders in the hidden mobile card).
		await expect(page.getByTestId('property-form')).toBeHidden();
		await page.getByTestId('property-search-input').fill(name);
		await expect(
			page.getByTestId('datagrid-desktop').getByTestId('property-name').filter({ hasText: name })
		).toBeVisible();
	});
});
