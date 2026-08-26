import { expect, test } from '@playwright/test';
import { loginWithApi } from './helpers';

test.describe('Dense form steppers', () => {
	test('splits lease details into narrower web steps', async ({ page, request }) => {
		await loginWithApi(page, request);

		const leasesLoaded = page.waitForResponse((response) =>
			response.url().includes('/api/v1/leases/page') && response.status() === 200
		);
		await page.goto('/leases', { waitUntil: 'domcontentloaded' });
		await expect(page.getByTestId('leases-page')).toBeVisible();
		await leasesLoaded;
		await expect(page.getByTestId('lease-create-button')).toBeEnabled();
		await page.getByTestId('lease-create-button').click();
		await expect(page.getByTestId('lease-stepper')).toBeVisible();
		await expect(page.getByTestId('lease-stepper-step-rent')).toContainText('Rent');
		await expect(page.getByTestId('lease-stepper-step-fees')).toContainText('Fees');
		await expect(page.getByTestId('lease-stepper-step-status')).toContainText('Status');
		await expect(page.getByTestId('lease-stepper-step-tracking')).toContainText('Tracking');
		await page.getByTestId('lease-form-cancel').click();
	});
});
