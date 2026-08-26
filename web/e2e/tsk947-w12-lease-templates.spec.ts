import { expect, test, type Page } from '@playwright/test';
import { login } from './helpers';

/** Open a sidebar group unless it is already showing the item we want to look at. */
async function openNavGroup(page: Page, groupId: string, probeTestId: string) {
	if (await page.getByTestId(probeTestId).isVisible()) return;
	await page.getByTestId(`nav-group-${groupId}`).click();
	await expect(page.getByTestId(probeTestId)).toBeVisible();
}

/**
 * TSK-947 W12 — the lease form lives in Settings, and sending a lease is one review.
 *
 * Setting up a lease form is a once-a-year job, so it moves out of the daily Rentals
 * list and into Settings as "Lease settings". Old links keep working. "Create and send
 * lease" shows the handful of things that matter — dates, rent, deposit, who signs —
 * and folds the rest away, with the signing order decided for the landlord.
 */
test.describe('Lease settings and one lease review', () => {
	test('moves the lease form into Settings and keeps the old link working', async ({ page }) => {
		await login(page);
		await page.waitForLoadState('networkidle');

		// Rentals is the daily list: no lease-form entry in it any more.
		await openNavGroup(page, 'rentals', 'nav-leases');
		await expect(page.getByTestId('nav-lease-templates')).toHaveCount(0);

		// Settings owns it now, under a plain name.
		await openNavGroup(page, 'settings', 'nav-settings-lease-templates');
		const leaseSettings = page.getByTestId('nav-settings-lease-templates');
		await expect(leaseSettings).toBeVisible();
		await expect(leaseSettings).toContainText('Lease settings');

		// Bookmarks and in-app links to the old address still land on the page.
		await page.goto('/lease-templates', { waitUntil: 'domcontentloaded' });
		await expect(page).toHaveURL(/\/settings\/lease-templates$/);
		await expect(page.getByTestId('lease-templates-page')).toBeVisible();
	});

	test('reviews a lease in one screen with the signing order already decided', async ({ page }) => {
		await login(page);

		await page.goto('/leases', { waitUntil: 'domcontentloaded' });
		await expect(page.getByTestId('leases-page')).toBeVisible();
		await page.waitForLoadState('networkidle');

		// A lease still being prepared is the one with an editable draft on it.
		const rows = page.getByRole('row');
		await expect(rows.first()).toBeVisible({ timeout: 15_000 });

		const editDraft = page.getByRole('button', { name: 'Edit draft' });
		const rowCount = Math.min(await rows.count(), 6);
		let found = false;
		for (let index = 1; index < rowCount; index += 1) {
			await page.goto('/leases', { waitUntil: 'domcontentloaded' });
			await expect(page.getByTestId('leases-page')).toBeVisible();
			await page.waitForLoadState('networkidle');
			await rows.nth(index).click();
			await expect(page.getByTestId('lease-lifecycle-actions')).toBeVisible({ timeout: 15_000 });
			if ((await editDraft.count()) > 0) {
				found = true;
				break;
			}
		}
		test.skip(!found, 'No seeded lease with an editable draft agreement.');

		await editDraft.first().click();
		await expect(page.getByTestId('agreement-draft-dialog')).toBeVisible();

		// One review: the things that decide the deal are on the screen.
		const review = page.getByTestId('lease-send-review');
		await expect(review).toBeVisible();
		await expect(page.getByTestId('agreement-draft-term-start')).toBeVisible();
		await expect(page.getByTestId('agreement-draft-rent')).toBeVisible();
		await expect(page.getByTestId('agreement-draft-deposit')).toBeVisible();
		await expect(page.getByTestId('agreement-draft-signer-name-0')).toBeVisible();
		await expect(page.getByTestId('agreement-draft-signer-email-0')).toBeVisible();
		await expect(page.getByTestId('agreement-draft-preview')).toBeVisible();
		await expect(page.getByTestId('agreement-draft-send')).toContainText('Send lease to sign');

		// The paperwork details stay folded away until asked for.
		const moreDetails = page.getByTestId('agreement-draft-more-details');
		await expect(moreDetails).toHaveJSProperty('open', false);
		await expect(page.getByTestId('agreement-draft-number')).toBeHidden();

		// Tenant signs, landlord countersigns — no order or role pickers to answer.
		const signingOrder = page.getByTestId('lease-signing-order');
		await expect(signingOrder).toHaveJSProperty('open', false);
		await expect(page.getByTestId('agreement-draft-signer-order-0')).toBeHidden();
		await expect(page.getByTestId('agreement-draft-signer-role-0')).toBeHidden();

		// They are still reachable for the landlord who wants them.
		await signingOrder.locator('summary').click();
		await expect(page.getByTestId('agreement-draft-signer-order-0')).toBeVisible();
		await expect(page.getByTestId('agreement-draft-signer-role-0')).toBeVisible();

		// Happy path: change the deposit on the review and save the draft.
		const deposit = page.getByTestId('agreement-draft-deposit');
		const current = Number((await deposit.inputValue()) || '0');
		await deposit.fill(String(current + 1));
		const saved = page.waitForResponse(
			(response) => response.request().method() === 'PATCH' && response.url().includes('/draft') && response.status() < 400
		);
		await page.getByTestId('agreement-draft-save').click();
		await saved;
		await expect(page.getByText('Lease draft saved.', { exact: false })).toBeVisible();
	});
});
