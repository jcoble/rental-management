import { expect, test, type APIRequestContext } from '@playwright/test';
import { apiToken, bearer, login, unique } from './helpers';

/**
 * TSK-947 W10 — the Listing tab stops mirroring Zillow.
 * One review sheet (headline, description, rent, deposit, photos) with two actions;
 * the posting bookkeeping folds away, and the automatic-posting card is gone while
 * that connection is unavailable. Applications are shared straight from the unit.
 */

type UnitRow = { id: number; propertyId: number; status?: string | number };

async function vacantUnitId(request: APIRequestContext, token: string): Promise<number> {
	const res = await request.get('/api/v1/units?take=500', { headers: bearer(token) });
	expect(res.ok(), `units list failed: ${res.status()}`).toBeTruthy();
	const units = (await res.json()) as UnitRow[];
	expect(units.length, 'seeded database has no units').toBeGreaterThan(0);
	const vacant = units.find((u) => u.status === 'Vacant' || u.status === 0);
	return (vacant ?? units[0]).id;
}

test.describe('Unit listing review sheet', () => {
	test('shows the four listing values up front and folds the posting details away', async ({
		page,
		request,
	}) => {
		const token = await apiToken(request);
		const unitId = await vacantUnitId(request, token);

		// A listing must exist before the sheet has anything to review.
		const prepare = await request.post(`/api/v1/units/${unitId}/listing-workspace/generate`, {
			headers: { ...bearer(token), 'Idempotency-Key': unique('e2e-listing-generate') },
		});
		expect(prepare.ok(), `prepare failed: ${prepare.status()}`).toBeTruthy();

		await login(page);
		await page.goto(`/units/${unitId}?tab=leasing&view=listing`);
		await expect(page.getByTestId('unit-listing-tab')).toBeVisible({ timeout: 15_000 });

		// The essentials are all on screen without opening anything.
		const essentials = page.getByTestId('listing-essentials');
		await expect(essentials).toBeVisible({ timeout: 15_000 });
		await expect(page.getByTestId('listing-headline-input')).toBeVisible();
		await expect(page.getByTestId('listing-description-input')).toBeVisible();
		await expect(page.getByTestId('listing-rent-input')).toBeVisible();
		await expect(page.getByTestId('listing-deposit-input')).toBeVisible();

		// Two actions, and only two.
		await expect(page.getByTestId('listing-copy-all')).toBeVisible();
		await expect(page.getByTestId('listing-open-zillow')).toBeVisible();

		// Posting bookkeeping starts closed.
		const posting = page.getByTestId('listing-posting-details');
		await expect(posting).toHaveJSProperty('open', false);
		await expect(page.getByText('Copy entered in Zillow')).toBeHidden();
		await posting.getByText('Posting details').click();
		await expect(posting).toHaveJSProperty('open', true);
		await expect(page.getByText('Copy entered in Zillow')).toBeVisible();

		// No automatic-posting card while that connection is unavailable.
		await expect(page.getByTestId('listing-connected')).toHaveCount(0);
		await expect(page.getByText('Automatic posting')).toHaveCount(0);

		// A happy-path save of the essentials.
		const headline = `Bright unit ${Date.now()}`;
		await page.getByTestId('listing-headline-input').fill(headline);
		await page.getByTestId('listing-description-input').fill('Freshly painted with in-unit laundry.');
		await page.getByTestId('listing-rent-input').fill('1750');
		await page.getByTestId('listing-deposit-input').fill('1750');
		const [saved] = await Promise.all([
			page.waitForResponse(
				(response) =>
					response.request().method() === 'PUT' &&
					response.url().includes(`/api/v1/units/${unitId}/listing-workspace`)
			),
			page.getByRole('button', { name: 'Save', exact: true }).click(),
		]);
		expect(saved.ok(), `save failed: ${saved.status()}`).toBeTruthy();

		const after = await request.get(`/api/v1/units/${unitId}/listing-workspace`, {
			headers: bearer(token),
		});
		expect(after.ok(), `listing lookup failed: ${after.status()}`).toBeTruthy();
		expect(await after.json()).toMatchObject({ headline, rent: 1750, securityDeposit: 1750 });
	});

	test('offers Share application at the top of an empty unit', async ({ page, request }) => {
		const token = await apiToken(request);
		const unitId = await vacantUnitId(request, token);

		await login(page);
		await page.goto(`/units/${unitId}?tab=leasing&view=applications`);
		await expect(page.getByTestId('unit-applications-tab')).toBeVisible({ timeout: 15_000 });

		const share = page.getByTestId('unit-application-share');
		await expect(share).toBeVisible();
		await expect(share).toHaveText(/Share application/);
		await expect(page.getByText('Create application link')).toHaveCount(0);

		await share.click();
		await expect(page.getByTestId('unit-application-link-url')).not.toHaveValue('');
		await expect(page.getByTestId('unit-application-link-copy')).toHaveText(/Copy link/);
	});
});
