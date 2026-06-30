import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { apiToken, bearer, login, unique } from './helpers';

interface SeededExpenseContext {
	propertyName: string;
	unitNumber: string;
	vendorOneName: string;
	vendorTwoName: string;
	workOrderTitle: string;
	workOrderDateUs: string;
	vendorOneAddress: string;
	vendorOnePhone: string;
	vendorOneTaxId: string;
}

async function createJson<T>(
	request: APIRequestContext,
	path: string,
	token: string,
	data: Record<string, unknown>
): Promise<T> {
	const response = await request.post(path, {
		headers: bearer(token),
		data
	});
	expect(response.ok(), `${path} failed: ${response.status()} ${await response.text()}`).toBeTruthy();
	return (await response.json()) as T;
}

async function selectOption(page: Page, triggerTestId: string, optionName: string | RegExp) {
	await page.getByTestId(triggerTestId).click();
	await page.getByRole('option', { name: optionName }).click();
}

async function nextExpenseStep(page: Page) {
	await page.getByTestId('expense-step-next').click();
	await page.waitForTimeout(350);
}

async function backExpenseStep(page: Page) {
	await page.getByTestId('expense-step-back').click();
	await page.waitForTimeout(50);
}

async function seedExpenseContext(request: APIRequestContext): Promise<SeededExpenseContext> {
	const token = await apiToken(request);
	const suffix = unique('TSK605');
	const propertyName = `${suffix} Property`;
	const unitNumber = `${suffix.slice(-5)}A`;
	const vendorOneName = `${suffix} Plumbing`;
	const vendorTwoName = `${suffix} Electric`;
	const workOrderTitle = `${suffix} disposal repair`;

	const property = await createJson<{ id: number }>(request, '/api/v1/properties', token, {
		name: propertyName,
		type: 'SingleFamily',
		addressLine1: '605 Cypress Lane',
		city: 'Austin',
		state: 'TX',
		postalCode: '78701',
		clearOwnerEntity: true
	});
	const unit = await createJson<{ id: number }>(request, '/api/v1/units', token, {
		propertyId: property.id,
		unitNumber,
		bedrooms: 2,
		bathrooms: 1,
		marketRent: 1450,
		status: 'Vacant'
	});
	const vendorOne = await createJson<{ id: number }>(request, '/api/v1/vendors', token, {
		name: vendorOneName,
		serviceType: 'Plumbing',
		phone: '(614) 555-0101',
		taxId: '12-3456789',
		addressLine1: '88 Maple Ave',
		city: 'Columbus',
		state: 'OH',
		postalCode: '43201'
	});
	const vendorTwo = await createJson<{ id: number }>(request, '/api/v1/vendors', token, {
		name: vendorTwoName,
		serviceType: 'Electrical',
		phone: '(614) 555-0202',
		taxId: '98-7654321',
		addressLine1: '99 Oak St',
		city: 'Dayton',
		state: 'OH',
		postalCode: '45402'
	});
	await createJson<{ id: number }>(request, '/api/v1/work-orders', token, {
		propertyId: property.id,
		unitId: unit.id,
		vendorId: vendorOne.id,
		title: workOrderTitle,
		description: 'Garbage disposal is leaking under the sink.',
		category: 'Plumbing',
		priority: 'Normal',
		status: 'Completed',
		completedAt: '2026-06-15T12:00:00Z',
		actualCost: 275.5
	});

	return {
		propertyName,
		unitNumber,
		vendorOneName,
		vendorTwoName,
		workOrderTitle,
		workOrderDateUs: '06/15/2026',
		vendorOneAddress: '88 Maple Ave, Columbus, OH 43201',
		vendorOnePhone: '(614) 555-0101',
		vendorOneTaxId: '12-3456789'
	};
}

test.describe('Accounting expense autofill', () => {
	test('prefills safe expense fields from selected work order and vendor', async ({ page, request }) => {
		const seeded = await seedExpenseContext(request);

		await login(page);
		await page.goto('/accounting');
		await expect(page.getByTestId('accounting-page')).toBeVisible();
		await page.waitForLoadState('networkidle');

		await page.getByTestId('expense-create-button').click();
		await expect(page.getByTestId('expense-stepper')).toBeVisible();

		await selectOption(page, 'expense-workorder-input', seeded.workOrderTitle);
		await expect(page.getByTestId('expense-property-input')).toContainText(seeded.propertyName);
		await expect(page.getByTestId('expense-unit-input')).toContainText(seeded.unitNumber);
		await expect(page.getByTestId('expense-vendor-input')).toContainText(seeded.vendorOneName);

		await nextExpenseStep(page);
		await expect(page.getByTestId('expense-description-input')).toHaveValue(seeded.workOrderTitle);
		await expect(page.getByTestId('expense-amount-input')).toHaveValue('275.5');

		await nextExpenseStep(page);
		await expect(page.getByTestId('expense-incurred-input')).toHaveValue(seeded.workOrderDateUs);

		await nextExpenseStep(page);
		await nextExpenseStep(page);
		await expect(page.getByTestId('expense-vendor-address-input')).toHaveValue(seeded.vendorOneAddress);
		await expect(page.getByTestId('expense-vendor-phone-input')).toHaveValue(seeded.vendorOnePhone);
		await expect(page.getByTestId('expense-vendor-taxid-input')).toHaveValue(seeded.vendorOneTaxId);

		await page.getByTestId('expense-vendor-address-input').fill('Manual address stays');
		await backExpenseStep(page);
		await backExpenseStep(page);
		await backExpenseStep(page);
		await backExpenseStep(page);
		await selectOption(page, 'expense-vendor-input', seeded.vendorTwoName);
		await nextExpenseStep(page);
		await nextExpenseStep(page);
		await nextExpenseStep(page);
		await nextExpenseStep(page);
		await expect(page.getByTestId('expense-vendor-address-input')).toHaveValue('Manual address stays');
	});
});
