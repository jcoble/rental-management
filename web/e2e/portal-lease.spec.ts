import { test, expect } from '@playwright/test';
import { login } from './helpers';

test.describe('Portal lease Q&A', () => {
	test('suggestion chips submit the selected question text', async ({ page }) => {
		let submittedQuestion: string | undefined;

		await page.route('**/api/v1/portal/leases', async (route) => {
			await route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify([
					{
						id: 44,
						leaseNumber: 'LEASE-PORTAL-44',
						propertyName: 'Maple Grove Duplex',
						unitNumber: 'A',
						monthlyRent: 1400,
						endDate: '2027-06-22',
					},
				]),
			});
		});

		await page.route('**/api/v1/portal/lease/ask?leaseId=44', async (route) => {
			const body = route.request().postDataJSON() as { question?: string };
			submittedQuestion = body.question;
			await route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify({
					answer: 'Pets require written approval from management.',
					sources: ['Lease addenda'],
				}),
			});
		});

		await login(page);
		await page.goto('/portal/lease');
		await expect(page.getByTestId('portal-lease-qa-card')).toBeVisible();

		await page.getByRole('button', { name: 'Can I have a pet?' }).click();

		await expect(page.getByTestId('portal-lease-question-answer')).toContainText(
			'Pets require written approval',
		);
		expect(submittedQuestion).toBe('Can I have a pet?');
		await expect(page.getByTestId('portal-lease-question-input')).toHaveValue('Can I have a pet?');
	});
});
