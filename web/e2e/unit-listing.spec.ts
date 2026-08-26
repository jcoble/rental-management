import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiToken, bearer, login, unique } from './helpers';

type UnitRow = {
	id: number;
	propertyId: number;
	status?: string | number;
};

type ListingPublication = {
	providerKey: string;
	mode: 'Guided' | 'Connected';
	status: string;
	listingUrl?: string | null;
	applicationUrl?: string | null;
	lastConfirmedExternalStatus?: string | null;
	copyConfirmed: boolean;
	termsConfirmed: boolean;
	photosConfirmed: boolean;
	needsRepublish: boolean;
	publishedContentVersion?: number | null;
};

type ListingWorkspace = {
	id: number;
	unitId: number;
	propertyId: number;
	contentVersion: number;
	headline: string;
	description: string;
	rent: number;
	securityDeposit?: number | null;
	leaseTerms?: string | null;
	petPolicy?: string | null;
	utilities?: string | null;
	parking?: string | null;
	amenities?: string | null;
	photoManifest: Array<{ position: number; category: string }>;
	publications: ListingPublication[];
	signedLeaseImportUrl: string;
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
		headers: { ...bearer(token), 'Idempotency-Key': unique('e2e-sandbox-choice') },
		data: { mode: 'sandbox' },
	});
	expect(res.ok(), `sandbox choice failed: ${res.status()}`).toBeTruthy();
}

async function readListingWorkspace(
	request: APIRequestContext,
	token: string,
	unitId: number
): Promise<ListingWorkspace> {
	const res = await request.get(`/api/v1/units/${unitId}/listing-workspace`, {
		headers: bearer(token),
	});
	expect(res.ok(), `listing workspace lookup failed: ${res.status()}`).toBeTruthy();
	return (await res.json()) as ListingWorkspace;
}

function guidedPublication(workspace: ListingWorkspace): ListingPublication {
	const publication = workspace.publications.find(
		(item) => item.providerKey === 'Zillow' && item.mode === 'Guided'
	);
	expect(publication, 'workspace has no guided Zillow publication').toBeTruthy();
	return publication!;
}

test.describe('Unit listing workspace', () => {
	test('prepares, publishes, and flags changed Zillow Guided copy for republishing', async ({
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

		await page.goto(`/units/${unitId}?tab=leasing&view=listing`);
		await expect(page.getByTestId('unit-page')).toBeVisible({ timeout: 15_000 });
		await expect(page.getByTestId('unit-listing-tab')).toBeVisible({ timeout: 15_000 });

		const [generateResponse] = await Promise.all([
			page.waitForResponse(
				(response) =>
					response.request().method() === 'POST' &&
					response.url().includes(`/api/v1/units/${unitId}/listing-workspace/generate`)
			),
			page.getByRole('button', { name: /Prepare listing|Sync unit details/ }).first().click(),
		]);
		expect(generateResponse.ok(), `prepare failed: ${generateResponse.status()}`).toBeTruthy();
		await expect(page.getByRole('heading', { name: 'Listing', exact: true })).toBeVisible({ timeout: 15_000 });

		const prepared = await readListingWorkspace(request, token, unitId);
		expect(prepared.unitId).toBe(unitId);
		expect(prepared.photoManifest.length).toBeGreaterThan(0);
		expect(prepared.photoManifest.map((photo) => photo.position)).toEqual(
			[...prepared.photoManifest.map((photo) => photo.position)].sort((a, b) => a - b)
		);
		expect(prepared.publications).toEqual(
			expect.arrayContaining([
				expect.objectContaining({ providerKey: 'Zillow', mode: 'Guided' }),
				expect.objectContaining({ providerKey: 'Zillow', mode: 'Connected' }),
			])
		);

		const marker = unique('zillow');
		const headline = `Move-in ready rental ${marker}`;
		const revisedHeadline = `${headline} updated`;
		const description = `Zillow handoff description ${marker}. Confirmed through the browser flow.`;
		const listingUrl = `https://www.zillow.com/homedetails/${marker}`;
		const applicationUrl = `https://www.zillow.com/renter-hub/applications/${marker}`;

		await page.getByLabel('Headline').fill(headline);
		await page.getByLabel('Description').fill(description);
		await page.getByLabel('Rent').fill('2125');
		await page.getByLabel('Deposit').fill('2125');
		await page.getByTestId('listing-more-details').getByText('More about this rental').click();
		await page.getByLabel('Lease terms').fill('12-month lease; renter pays utilities');
		await page.getByLabel('Pet policy').fill('Pets considered case by case');
		await page.getByLabel('Utilities').fill('Tenant pays electric and gas');
		await page.getByLabel('Parking').fill('One off-street parking spot');
		await page.getByLabel('Amenities').fill('In-unit laundry, central air, quiet street');
		await page.getByTestId('listing-posting-details').getByText('Posting details').click();
		await page.getByText('Where is this listing in the publishing process?').locator('..').getByRole('button').click();
		await page.getByRole('option', { name: 'Published', exact: true }).click();
		await page.getByLabel('Copy entered in Zillow').check();
		await page.getByLabel('Terms reviewed in Zillow').check();
		await page.getByLabel('Photos uploaded in order').check();
		await page.getByLabel('Listing URL').fill(listingUrl);
		await page.getByLabel('Application URL').fill(applicationUrl);
		await page.getByLabel('Last status you confirmed').fill('Active');

		const [publishResponse] = await Promise.all([
			page.waitForResponse(
				(response) =>
					response.request().method() === 'PUT' &&
					response.url().includes(`/api/v1/units/${unitId}/listing-workspace`)
			),
			page.getByRole('button', { name: 'Save as published in Zillow' }).click(),
		]);
		expect(publishResponse.ok(), `publish save failed: ${publishResponse.status()}`).toBeTruthy();

		await expect
			.poll(async () => guidedPublication(await readListingWorkspace(request, token, unitId)))
			.toMatchObject({
				status: 'Published',
				listingUrl,
				applicationUrl,
				lastConfirmedExternalStatus: 'Active',
				copyConfirmed: true,
				termsConfirmed: true,
				photosConfirmed: true,
				needsRepublish: false,
			});
		const published = await readListingWorkspace(request, token, unitId);
		expect(published).toMatchObject({
			headline,
			description,
			rent: 2125,
			securityDeposit: 2125,
			leaseTerms: '12-month lease; renter pays utilities',
			petPolicy: 'Pets considered case by case',
			utilities: 'Tenant pays electric and gas',
			parking: 'One off-street parking spot',
			amenities: 'In-unit laundry, central air, quiet street',
		});

		await page.getByLabel('Headline').fill(revisedHeadline);
		const [saveResponse] = await Promise.all([
			page.waitForResponse(
				(response) =>
					response.request().method() === 'PUT' &&
					response.url().includes(`/api/v1/units/${unitId}/listing-workspace`)
			),
			page.getByRole('button', { name: 'Save', exact: true }).click(),
		]);
		expect(saveResponse.ok(), `content save failed: ${saveResponse.status()}`).toBeTruthy();

		await expect(page.getByText('Changes need republishing.')).toBeVisible();
		const changed = await readListingWorkspace(request, token, unitId);
		expect(changed.headline).toBe(revisedHeadline);
		expect(changed.contentVersion).toBeGreaterThan(published.contentVersion);
		expect(guidedPublication(changed).needsRepublish).toBeTruthy();

		await page.getByTestId('listing-copy-all').click();
		await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toContain(revisedHeadline);

		const signedLeaseLink = page.getByRole('link', { name: 'Import signed Zillow lease' });
		await expect(signedLeaseLink).toHaveAttribute(
			'href',
			`/scan?type=LeaseAgreement&propertyId=${changed.propertyId}&unitId=${unitId}&rentalListingId=${changed.id}&sourceLabel=Zillow%20signed%20lease%20import&returnTo=%2Funits%2F${unitId}%3Ftab%3Dtenant-lease%26view%3Dagreements`
		);
	});

	test('does not save a listing workspace without required copy', async ({ page, request }) => {
		const token = await apiToken(request);
		await ensureSandboxChoice(request, token);
		const unitId = await findListingUnit(request, token);

		const prepare = await request.post(`/api/v1/units/${unitId}/listing-workspace/generate`, {
			headers: { ...bearer(token), 'Idempotency-Key': unique('e2e-listing-generate') },
		});
		expect(prepare.ok(), `prepare failed: ${prepare.status()}`).toBeTruthy();

		await login(page);
		await page.goto(`/units/${unitId}?tab=leasing&view=listing`);
		await expect(page.getByRole('heading', { name: 'Listing', exact: true })).toBeVisible({ timeout: 15_000 });

		const save = page.getByRole('button', { name: 'Save', exact: true });
		await expect(save).toBeEnabled();
		await page.getByLabel('Headline').fill('');
		await expect(save).toBeDisabled();
		await page.getByLabel('Headline').fill('Valid listing headline');
		await page.getByLabel('Description').fill('');
		await expect(save).toBeDisabled();
	});
});
