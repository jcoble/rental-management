import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiToken, bearer, login, unique } from './helpers';

type UnitRow = {
	id: number;
	status?: string | number;
};

type UnitListing = {
	id: number;
	unitId: number;
	status: string;
	headline: string;
	description: string;
	rent: number;
	securityDeposit?: number | null;
	leaseTerms?: string | null;
	petPolicy?: string | null;
	utilities?: string | null;
	parking?: string | null;
	amenities?: string | null;
	photoNotes?: string | null;
	zillowListingUrl?: string | null;
	zillowApplicationUrl?: string | null;
	isPosted: boolean;
	postedAtUtc?: string | null;
};

async function findListingUnit(request: APIRequestContext, token: string): Promise<number> {
	const res = await request.get('/api/v1/units?take=500', { headers: bearer(token) });
	expect(res.ok(), `units list failed: ${res.status()}`).toBeTruthy();
	const units = (await res.json()) as UnitRow[];
	expect(units.length, 'seeded database has no units').toBeGreaterThan(0);

	const vacant = units.find((u) => u.status === 'Vacant' || u.status === 0);
	return (vacant ?? units[0]).id;
}

async function ensureSandboxChoice(request: APIRequestContext, token: string): Promise<void> {
	const res = await request.post('/api/v1/portfolio/onboarding-choice', {
		headers: bearer(token),
		data: { mode: 'sandbox' },
	});
	expect(res.ok(), `sandbox choice failed: ${res.status()}`).toBeTruthy();
}

async function readListing(
	request: APIRequestContext,
	token: string,
	unitId: number
): Promise<UnitListing> {
	const res = await request.get(`/api/v1/units/${unitId}/listing`, { headers: bearer(token) });
	expect(res.ok(), `listing lookup failed: ${res.status()}`).toBeTruthy();
	return (await res.json()) as UnitListing;
}

test.describe('Unit listing handoff', () => {
	test('generates, edits, saves, copies, and persists a Zillow manual listing packet', async ({
		page,
		request,
		context,
	}) => {
		const baseUrl = process.env.PW_BASE_URL ?? 'https://localhost:5667';
		await context.grantPermissions(['clipboard-read', 'clipboard-write'], {
			origin: new URL(baseUrl).origin,
		});

		const token = await apiToken(request);
		await ensureSandboxChoice(request, token);
		const unitId = await findListingUnit(request, token);
		await login(page);

		await page.goto(`/units/${unitId}?tab=listing`);
		await expect(page.getByTestId('unit-page')).toBeVisible({ timeout: 15_000 });
		await expect(page.getByTestId('unit-listing-tab')).toBeVisible({ timeout: 15_000 });

		const [generateResponse] = await Promise.all([
			page.waitForResponse(
				(response) =>
					response.request().method() === 'POST' &&
					response.url().includes(`/api/v1/units/${unitId}/listing/generate`)
			),
			page.getByTestId('listing-generate').click(),
		]);
		expect(generateResponse.ok(), `generate failed: ${generateResponse.status()}`).toBeTruthy();
		await expect(page.getByTestId('listing-editor')).toBeVisible({ timeout: 15_000 });

		const marker = unique('zillow');
		const headline = `Move-in ready rental ${marker}`;
		const description = `Zillow handoff description ${marker}. Confirmed through the browser flow.`;
		const listingUrl = `https://www.zillow.com/homedetails/${marker}`;
		const applicationUrl = `https://www.zillow.com/renter-hub/applications/${marker}`;

		await page.getByTestId('listing-headline').fill(headline);
		await page.getByTestId('listing-description').fill(description);
		await page.getByTestId('listing-rent').fill('2125');
		await page.getByTestId('listing-deposit').fill('2125');
		await page.getByTestId('listing-lease-terms').fill('12-month lease; renter pays utilities');
		await page.getByTestId('listing-pet-policy').fill('Pets considered case by case');
		await page.getByTestId('listing-utilities').fill('Tenant pays electric and gas');
		await page.getByTestId('listing-parking').fill('One off-street parking spot');
		await page.getByTestId('listing-amenities').fill('In-unit laundry, central air, quiet street');
		await page.getByTestId('listing-photo-notes').fill('Use exterior, kitchen, bath, and bedroom photos');
		await page.getByTestId('listing-status').selectOption('Posted');
		await page.getByTestId('listing-zillow-url').fill(listingUrl);
		await page.getByTestId('listing-zillow-application-url').fill(applicationUrl);

		await expect(page.getByTestId('listing-save')).toBeEnabled();
		const [saveResponse] = await Promise.all([
			page.waitForResponse(
				(response) =>
					response.request().method() === 'PUT' &&
					response.url().includes(`/api/v1/units/${unitId}/listing`)
			),
			page.getByTestId('listing-save').click(),
		]);
		expect(
			saveResponse.ok(),
			`save failed: ${saveResponse.status()} ${await saveResponse.text()}`
		).toBeTruthy();

		await expect(async () => {
			const saved = await readListing(request, token, unitId);
			expect(saved.headline).toBe(headline);
			expect(saved.description).toBe(description);
			expect(saved.status).toBe('Posted');
			expect(saved.isPosted).toBeTruthy();
			expect(saved.rent).toBe(2125);
			expect(saved.securityDeposit).toBe(2125);
			expect(saved.leaseTerms).toBe('12-month lease; renter pays utilities');
			expect(saved.petPolicy).toBe('Pets considered case by case');
			expect(saved.utilities).toBe('Tenant pays electric and gas');
			expect(saved.parking).toBe('One off-street parking spot');
			expect(saved.amenities).toBe('In-unit laundry, central air, quiet street');
			expect(saved.photoNotes).toBe('Use exterior, kitchen, bath, and bedroom photos');
			expect(saved.zillowListingUrl).toBe(listingUrl);
			expect(saved.zillowApplicationUrl).toBe(applicationUrl);
			expect(saved.postedAtUtc).toBeTruthy();
		}).toPass({ timeout: 10_000 });

		await page.reload();
		await expect(page.getByTestId('unit-listing-tab')).toBeVisible({ timeout: 15_000 });
		await expect(page.getByTestId('listing-headline')).toHaveValue(headline);
		await expect(page.getByTestId('listing-description')).toHaveValue(description);
		await expect(page.getByTestId('listing-status')).toHaveValue('Posted');
		await expect(page.getByTestId('listing-open-zillow')).toHaveAttribute(
			'href',
			'https://www.zillow.com/rental-manager/properties'
		);

		await page.getByTestId('listing-copy-headline').click();
		await expect
			.poll(() => page.evaluate(() => navigator.clipboard.readText()))
			.toBe(headline);
	});

	test('keeps save disabled when required listing copy is missing', async ({ page, request }) => {
		const token = await apiToken(request);
		await ensureSandboxChoice(request, token);
		const unitId = await findListingUnit(request, token);
		await login(page);

		await page.goto(`/units/${unitId}?tab=listing`);
		await expect(page.getByTestId('unit-listing-tab')).toBeVisible({ timeout: 15_000 });
		const [generateResponse] = await Promise.all([
			page.waitForResponse(
				(response) =>
					response.request().method() === 'POST' &&
					response.url().includes(`/api/v1/units/${unitId}/listing/generate`)
			),
			page.getByTestId('listing-generate').click(),
		]);
		expect(generateResponse.ok(), `generate failed: ${generateResponse.status()}`).toBeTruthy();
		await expect(page.getByTestId('listing-editor')).toBeVisible({ timeout: 15_000 });

		await expect(page.getByTestId('listing-save')).toBeEnabled();
		await page.getByTestId('listing-headline').fill('');
		await expect(page.getByTestId('listing-save')).toBeDisabled();
		await page.getByTestId('listing-headline').fill('Valid listing headline');
		await page.getByTestId('listing-description').fill('');
		await expect(page.getByTestId('listing-save')).toBeDisabled();
	});
});
