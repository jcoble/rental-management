import { expect, test } from '@playwright/test';
import { login } from './helpers';

/**
 * TSK-947 W4 + W5. The dashboard opens on one ranked "Needs attention" list, the Money nav
 * starts with "Who's behind", and recording a payment asks for three things up front with
 * everything else tucked under "More details".
 */
test.describe('dashboard needs attention and record payment', () => {
	test('the needs-attention list is the first thing on the dashboard', async ({ page }) => {
		await login(page);
		await page.goto('/', { waitUntil: 'domcontentloaded' });

		const attention = page.getByTestId('needs-attention');
		await expect(attention).toBeVisible();

		const box = await attention.boundingBox();
		expect(box, 'the needs-attention list should have a layout box').not.toBeNull();
		expect(box!.y).toBeLessThan(400);

		// The rest of the dashboard is back, but it all sits below the list.
		const hero = page.getByTestId('dashboard-hero');
		await expect(hero).toBeVisible();
		const heroBox = await hero.boundingBox();
		expect(heroBox, 'the portfolio band should have a layout box').not.toBeNull();
		expect(heroBox!.y).toBeGreaterThan(box!.y);
	});

	test('the Money nav starts with Who\'s behind', async ({ page }) => {
		await login(page);
		await page.goto('/', { waitUntil: 'domcontentloaded' });
		await page.waitForLoadState('networkidle');

		const moneyGroup = page.getByTestId('nav-group-money');
		await expect(moneyGroup).toBeVisible();

		const whosBehind = page.getByTestId('nav-accounting-past-due');
		if (!(await whosBehind.isVisible())) {
			await moneyGroup.click();
		}
		await expect(whosBehind).toBeVisible();
		await expect(whosBehind).toContainText("Who's behind");

		// First in the group: it sits above the Overview link.
		const behindBox = await whosBehind.boundingBox();
		const overviewBox = await page.getByTestId('nav-accounting').boundingBox();
		expect(behindBox).not.toBeNull();
		expect(overviewBox).not.toBeNull();
		expect(behindBox!.y).toBeLessThan(overviewBox!.y);
	});

	test('recording a payment asks for three things, the rest under More details', async ({
		page
	}) => {
		await login(page);
		await page.goto('/', { waitUntil: 'domcontentloaded' });

		await expect(page.getByTestId('needs-attention')).toBeVisible();
		const recordPayment = page.getByTestId('needs-attention-record-payment').first();
		test.skip(
			(await page.getByTestId('needs-attention-record-payment').count()) === 0,
			'No tenant is behind in this sample data, so there is no Record payment row.'
		);

		await recordPayment.click();

		const dialog = page.getByTestId('past-due-mark-paid-dialog');
		await expect(dialog).toBeVisible();

		const essentials = page.getByTestId('payment-essentials');
		await expect(essentials).toBeVisible();
		await expect(essentials.getByTestId('past-due-mark-paid-amount-input')).toBeVisible();
		await expect(essentials.getByTestId('past-due-mark-paid-date-input')).toBeVisible();
		await expect(essentials.getByTestId('past-due-mark-paid-method-input')).toBeVisible();

		const moreDetails = page.getByTestId('payment-more-details');
		await expect(moreDetails).toBeVisible();
		await expect(moreDetails).not.toHaveAttribute('open', /.*/);
		await expect(page.getByTestId('past-due-mark-paid-reference-input')).toBeHidden();

		await page.getByTestId('payment-more-details-summary').click();
		await expect(page.getByTestId('past-due-mark-paid-reference-input')).toBeVisible();
		await expect(page.getByTestId('past-due-allocation-preview')).toBeVisible();
	});

	test('a recorded payment saves and clears the row', async ({ page }) => {
		await login(page);
		await page.goto('/', { waitUntil: 'domcontentloaded' });

		await expect(page.getByTestId('needs-attention')).toBeVisible();
		test.skip(
			(await page.getByTestId('needs-attention-record-payment').count()) === 0,
			'No tenant is behind in this sample data, so there is nothing to record.'
		);

		await page.getByTestId('needs-attention-record-payment').first().click();
		await expect(page.getByTestId('past-due-mark-paid-dialog')).toBeVisible();

		await page.getByTestId('past-due-mark-paid-amount-input').fill('1');
		await page.getByTestId('past-due-mark-paid-method-input').selectOption('Check');
		await page.getByTestId('past-due-mark-paid-confirm').click();

		await expect(page.getByTestId('past-due-mark-paid-dialog')).toBeHidden();
	});
});
