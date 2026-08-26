import { test, expect, type Page, type Route } from '@playwright/test';
import { login } from './helpers';

type ConversationSummary = {
	id: number;
	tenantId: number;
	tenantName: string;
	subject: string;
	propertyName: string;
	lastMessagePreview: string;
	lastMessageAt: string;
	unreadCount: number;
	messageCount: number;
};

function conversationSummary(id: number, subject = `Runtime conversation ${id}`): ConversationSummary {
	return {
		id,
		tenantId: 1,
		tenantName: 'Runtime Tenant',
		subject,
		propertyName: 'Runtime Property',
		lastMessagePreview: 'A runtime deep-link check',
		lastMessageAt: '2026-08-09T16:00:00Z',
		unreadCount: 0,
		messageCount: 1
	};
}

function conversation(id: number, subject = `Runtime conversation ${id}`) {
	return {
		...conversationSummary(id, subject),
		messages: [
			{
				id: id * 10,
				senderRole: 'Tenant',
				body: 'This thread was opened by the route-recovery runtime test.',
				createdAt: '2026-08-09T16:00:00Z'
			}
		]
	};
}

async function fulfillJson(route: Route, status: number, body: unknown) {
	await route.fulfill({
		status,
		contentType: 'application/json',
		body: JSON.stringify(body)
	});
}

function inspectionDetailForUnitSelection(id: number, unitId: number | null) {
	return {
		id,
		portfolioId: 1,
		propertyId: 1,
		unitId,
		type: 'Routine',
		status: 'Scheduled',
		scheduledFor: '2026-08-10T12:00:00Z',
		completedAt: null,
		outcome: null,
		notes: `Selection guard coverage ${id}`,
		propertyName: 'Runtime Property',
		unitNumber: unitId == null ? null : `Unit ${unitId}`,
		createdAt: '2026-08-10T12:00:00Z',
		updatedAt: '2026-08-10T12:00:00Z',
		items: []
	};
}

async function stubConversationDetail(page: Page, id: number, subject?: string, requestSequence?: string[]) {
	await page.route(`**/api/v1/conversations/${id}`, async (route) => {
		requestSequence?.push('detail GET');
		await fulfillJson(route, 200, conversation(id, subject));
	});
	await page.route(`**/api/v1/conversations/${id}/read`, async (route) => {
		requestSequence?.push('mark-read');
		await route.fulfill({ status: 204 });
	});
}

type UnitCase = {
	name: string;
	path: string;
	expectation: 'unit' | 'applications' | 'money' | 'work-orders' | 'error';
};

const UNIT_CASES: UnitCase[] = [
	{ name: 'valid direct unit', path: '/units/10', expectation: 'unit' },
	{ name: 'encoded unit id', path: '/units/%31%30', expectation: 'unit' },
	{ name: 'unknown unit', path: '/units/999999', expectation: 'error' },
	{
		name: 'leasing applications tab and query context',
		path: '/units/10?tab=leasing&view=applications',
		expectation: 'applications'
	},
	{
		name: 'money tenant-account tab and query context',
		path: '/units/10?tab=money&view=tenant-account&tenantAccount=11',
		expectation: 'money'
	},
	{
		name: 'unknown Money view falls back to the default',
		path: '/units/10?tab=money&view=unknown',
		expectation: 'money'
	},
	{
		name: 'work-orders deep link with work-order context',
		path: '/units/1?tab=money&view=work-orders&wo=3',
		expectation: 'work-orders'
	}
];

function isExpectedTransportNoise(message: string): boolean {
	return /SignalR|Failed to fetch|Failed to complete negotiation|WebSocket connection|INTERNET_DISCONNECTED/.test(message);
}

function routeLifecycleErrors(messages: string[]): string[] {
	const lifecycleErrors: string[] = [];
	for (const message of messages) {
		const hasCrashSignature = /effect_update_depth_exceeded|\$set|Cannot read properties of undefined/.test(message);
		if (hasCrashSignature) {
			lifecycleErrors.push(message);
			continue;
		}
		if (isExpectedTransportNoise(message)) continue;
		if (/TypeError/.test(message)) lifecycleErrors.push(message);
	}
	return lifecycleErrors;
}

async function assertUnitEntry(page: Page, unitCase: UnitCase, phase: string): Promise<void> {
	const contentTimeout = unitCase.expectation === 'work-orders' ? 30_000 : 15_000;
	await expect(page.getByTestId('unit-page'), `${unitCase.name} ${phase} shell`).toBeVisible({ timeout: contentTimeout });

	switch (unitCase.expectation) {
		case 'unit':
			await expect(page.getByTestId('unit-header'), `${unitCase.name} ${phase} header`).toBeVisible({ timeout: 15_000 });
			break;
		case 'applications':
			await expect(page.getByTestId('unit-applications-section'), `${unitCase.name} ${phase} panel`).toBeVisible({ timeout: 15_000 });
			break;
		case 'money':
			await expect(page.getByTestId('unit-rent-tab'), `${unitCase.name} ${phase} panel`).toBeVisible({ timeout: 15_000 });
			break;
		case 'work-orders':
			await expect(page.getByTestId('unit-work-orders-section'), `${unitCase.name} ${phase} panel`).toBeVisible({ timeout: contentTimeout });
			break;
		case 'error':
			await expect(page.getByTestId('unit-error'), `${unitCase.name} ${phase} error`).toBeVisible({ timeout: 15_000 });
			break;
	}
}

test.describe('Batch 1 route recovery at the browser lifecycle boundary', () => {
	test('cold-loads and refreshes valid, encoded, unknown, tab, money, unknown-view, and work-order unit URLs', async ({ page }) => {
		test.setTimeout(120_000);
		const failures: string[] = [];
		const consoleErrors: string[] = [];
		const pageErrors: string[] = [];

		page.on('console', (message) => {
			if (message.type() === 'error') consoleErrors.push(message.text());
		});
		page.on('pageerror', (error) => pageErrors.push(error.stack ?? String(error)));

		await login(page);
		for (const unitCase of UNIT_CASES) {
			for (const phase of ['cold', 'refresh']) {
				try {
					await page.goto(unitCase.path, { waitUntil: 'domcontentloaded' });
					if (phase === 'refresh') await page.reload({ waitUntil: 'domcontentloaded' });
					await assertUnitEntry(page, unitCase, phase);
				} catch (error) {
					failures.push(`${unitCase.name} (${phase}): ${error instanceof Error ? error.message : String(error)}`);
				}
			}
		}

		const lifecycleErrors = routeLifecycleErrors([...consoleErrors, ...pageErrors]);
		console.log(JSON.stringify({ UNIT_CASES, failures, lifecycleErrors }, null, 2));
		expect(lifecycleErrors, 'unit cold-load lifecycle errors').toEqual([]);
		expect(failures, 'unit cold-load/refresh failures').toEqual([]);
	});

	test('lets every primary tab leave a lease-management deep link', async ({ page }) => {
		test.setTimeout(180_000);
		const consoleErrors: string[] = [];
		const pageErrors: string[] = [];
		const deepLink = '/units/19?tab=tenant-lease&view=agreements&leaseManagement=17';
		const destinations: ReadonlyArray<{ tab: string; path: string }> = [
			{ tab: 'summary', path: '/units/19?tab=summary' },
			{ tab: 'leasing', path: '/units/19?tab=leasing&view=listing' },
			{ tab: 'money', path: '/units/19?tab=money&view=tenant-account' },
			{ tab: 'maintenance', path: '/units/19?tab=maintenance&view=work-orders' },
			{ tab: 'documents-history', path: '/units/19?tab=documents-history&view=documents' },
		];
		page.on('console', (message) => {
			if (message.type() === 'error') consoleErrors.push(message.text());
		});
		page.on('pageerror', (error) => pageErrors.push(error.stack ?? String(error)));

		await login(page);
		expect(await page.evaluate(() => ({ width: window.innerWidth, height: window.innerHeight }))).toEqual({ width: 1710, height: 990 });

		for (const { tab, path } of destinations) {
			// Recreate the reported deep state before every click so each primary
			// destination exercises the production goto boundary, not just the first one.
			await page.goto(deepLink, { waitUntil: 'domcontentloaded' });
			await expect(page.getByTestId('unit-page'), `${tab} deep-link shell`).toBeVisible({ timeout: 30_000 });
			await expect(page.getByTestId('tab-tenant-lease'), `${tab} owning tab`).toHaveAttribute('aria-selected', 'true');
			await expect(page.getByTestId('unit-agreement-section'), `${tab} owning detail section`).toBeVisible({ timeout: 30_000 });
			await expect(page.getByText('Lease at a glance', { exact: true }), `${tab} owning lease detail`).toBeVisible({ timeout: 30_000 });
			expect(new URL(page.url()).searchParams.get('leaseManagement'), `${tab} deep-link record`).toBe('17');
			const preClickLifecycleErrors = routeLifecycleErrors([...consoleErrors, ...pageErrors]);
			expect(preClickLifecycleErrors, `${tab} pre-click lifecycle errors`).toEqual([]);
			const consoleErrorStart = consoleErrors.length;
			const pageErrorStart = pageErrors.length;

			await page.getByTestId(`tab-${tab}`).click();
			await expect.poll(() => {
				const url = new URL(page.url());
				return `${url.pathname}${url.search}`;
			}, `${tab} destination URL`).toBe(path);
			await expect(page.getByTestId(`tab-${tab}`)).toHaveAttribute('aria-selected', 'true');
			await expect(page.getByTestId('tab-tenant-lease')).toHaveAttribute('aria-selected', 'false');
			expect(new URL(page.url()).searchParams.get('leaseManagement'), `${tab} foreign record`).toBeNull();
			const clickLifecycleErrors = routeLifecycleErrors([
				...consoleErrors.slice(consoleErrorStart),
				...pageErrors.slice(pageErrorStart),
			]);
			expect(clickLifecycleErrors, `${tab} click lifecycle errors`).toEqual([]);
		}

		const lifecycleErrors = routeLifecycleErrors([...consoleErrors, ...pageErrors]);
		console.log(JSON.stringify({ consoleErrors, pageErrors, lifecycleErrors }, null, 2));
		expect(lifecycleErrors, 'lease-management tab-click lifecycle errors').toEqual([]);
	});

	test('lets a nested Money view click win after deep-state arrival and on a fresh load', async ({ page }) => {
		test.setTimeout(120_000);
		const consoleErrors: string[] = [];
		const pageErrors: string[] = [];
		const evidenceDir = process.env.TSK848_EVIDENCE_DIR;
		page.on('console', (message) => {
			if (message.type() === 'error') consoleErrors.push(message.text());
		});
		page.on('pageerror', (error) => pageErrors.push(error.stack ?? String(error)));

		await login(page);
		expect(await page.evaluate(() => ({ width: window.innerWidth, height: window.innerHeight }))).toEqual({ width: 1710, height: 990 });

		await page.goto('/units/19?tab=tenant-lease&view=agreements&leaseManagement=17', { waitUntil: 'domcontentloaded' });
		await expect(page.getByTestId('unit-page')).toBeVisible({ timeout: 30_000 });
		await expect(page.getByTestId('unit-agreement-section')).toBeVisible({ timeout: 30_000 });
		await page.getByTestId('tab-money').click();
		await expect(page).toHaveURL('/units/19?tab=money&view=tenant-account');
		await expect(page.getByTestId('ledger-rent-panel')).toBeVisible({ timeout: 30_000 });
		if (evidenceDir) await page.screenshot({ path: `${evidenceDir}/tsk848-money-rent-after-deep-arrival-1710x990.png`, fullPage: true });

		await page.getByTestId('unit-money-view-operating-costs').click();
		await expect(page).toHaveURL('/units/19?tab=money&view=operating-costs');
		await expect(page.getByTestId('ledger-expenses-panel')).toBeVisible({ timeout: 30_000 });
		if (evidenceDir) await page.screenshot({ path: `${evidenceDir}/tsk848-money-expenses-after-nested-click-1710x990.png`, fullPage: true });

		await page.goto('/units/1?tab=money', { waitUntil: 'domcontentloaded' });
		await expect(page.getByTestId('unit-page')).toBeVisible({ timeout: 30_000 });
		await expect(page.getByTestId('ledger-rent-panel')).toBeVisible({ timeout: 30_000 });
		await page.getByTestId('unit-money-view-operating-costs').click();
		await expect(page).toHaveURL('/units/1?tab=money&view=operating-costs');
		await expect(page.getByTestId('ledger-expenses-panel')).toBeVisible({ timeout: 30_000 });
		await page.getByTestId('unit-money-view-tenant-account').click();
		await expect(page).toHaveURL('/units/1?tab=money&view=tenant-account');
		await expect(page.getByTestId('ledger-rent-panel')).toBeVisible({ timeout: 30_000 });
		if (evidenceDir) await page.screenshot({ path: `${evidenceDir}/tsk848-money-fresh-load-round-trip-1710x990.png`, fullPage: true });

		const lifecycleErrors = routeLifecycleErrors([...consoleErrors, ...pageErrors]);
		console.log(JSON.stringify({ consoleErrors, pageErrors, lifecycleErrors }, null, 2));
		expect(lifecycleErrors, 'nested Money lifecycle errors').toEqual([]);
	});

	test('opens a Unit inspection row, deep-links its facts, and closes back to the list', async ({ page }) => {
		test.setTimeout(120_000);
		const consoleErrors: string[] = [];
		const pageErrors: string[] = [];
		const evidenceDir = process.env.TSK848_EVIDENCE_DIR;
		page.on('console', (message) => {
			if (message.type() === 'error') consoleErrors.push(message.text());
		});
		page.on('pageerror', (error) => pageErrors.push(error.stack ?? String(error)));

		await login(page);
		expect(await page.evaluate(() => ({ width: window.innerWidth, height: window.innerHeight }))).toEqual({ width: 1710, height: 990 });
		await page.goto('/units/1?tab=maintenance&view=inspections', { waitUntil: 'domcontentloaded' });
		await expect(page.getByTestId('unit-inspections-section')).toBeVisible({ timeout: 30_000 });
		const inspectionRow = page.locator('[data-testid^="inspection-open-"]').first();
		await expect(inspectionRow, 'Unit inspection row').toBeVisible({ timeout: 30_000 });
		const rowTestId = await inspectionRow.getAttribute('data-testid');
		const inspectionId = rowTestId?.replace('inspection-open-', '');
		expect(inspectionId, 'inspection row id').toMatch(/^\d+$/);

		await inspectionRow.click();
		await expect(page).toHaveURL(new RegExp(`/units/1\\?tab=maintenance&view=inspections&inspection=${inspectionId}$`));
		await page.reload({ waitUntil: 'domcontentloaded' });
		await expect(page).toHaveURL(new RegExp(`/units/1\\?tab=maintenance&view=inspections&inspection=${inspectionId}$`));
		await expect(page.getByTestId('unit-inspection-detail-surface')).toBeVisible({ timeout: 30_000 });
		await expect(page.getByTestId('inspection-detail-facts')).toBeVisible({ timeout: 30_000 });
		await expect(page.getByTestId('inspection-detail-title')).toBeVisible();
		if (evidenceDir) await page.screenshot({ path: `${evidenceDir}/tsk848-inspection-detail-1710x990.png`, fullPage: true });

		await page.getByTestId('inspection-detail-close').click();
		await expect(page).toHaveURL('/units/1?tab=maintenance&view=inspections');
		await expect(page.getByTestId('unit-inspection-list')).toBeVisible({ timeout: 30_000 });

		const lifecycleErrors = routeLifecycleErrors([...consoleErrors, ...pageErrors]);
		console.log(JSON.stringify({ consoleErrors, pageErrors, lifecycleErrors }, null, 2));
		expect(lifecycleErrors, 'inspection detail lifecycle errors').toEqual([]);
	});

	test('clears wrong-Unit and null-Unit inspection deep links back to the list', async ({ page }) => {
		test.setTimeout(120_000);
		const consoleErrors: string[] = [];
		const pageErrors: string[] = [];
		const cases = [
			{ name: 'wrong-Unit inspection', inspectionId: 84801, unitId: 2 },
			{ name: 'null-Unit inspection', inspectionId: 84802, unitId: null }
		] as const;
		page.on('console', (message) => {
			if (message.type() === 'error') consoleErrors.push(message.text());
		});
		page.on('pageerror', (error) => pageErrors.push(error.stack ?? String(error)));

		await login(page);
		expect(await page.evaluate(() => ({ width: window.innerWidth, height: window.innerHeight }))).toEqual({ width: 1710, height: 990 });

		for (const invalidCase of cases) {
			const detailRoute = `**/api/v1/inspections/${invalidCase.inspectionId}`;
			let detailRequests = 0;
			await page.route(detailRoute, async (route) => {
				detailRequests += 1;
				await fulfillJson(route, 200, inspectionDetailForUnitSelection(invalidCase.inspectionId, invalidCase.unitId));
			});

			await page.goto(`/units/1?tab=maintenance&view=inspections&inspection=${invalidCase.inspectionId}`, { waitUntil: 'domcontentloaded' });
			await expect.poll(() => detailRequests, `${invalidCase.name} detail request`).toBeGreaterThan(0);
			await expect(page).toHaveURL('/units/1?tab=maintenance&view=inspections', { timeout: 30_000 });
			await expect(page.getByTestId('unit-inspection-list'), `${invalidCase.name} list`).toBeVisible({ timeout: 30_000 });
			await expect(page.getByTestId('inspection-detail-facts'), `${invalidCase.name} facts`).toHaveCount(0);
			await expect(page.getByTestId('inspection-detail-title'), `${invalidCase.name} title`).toHaveCount(0);
			await page.unroute(detailRoute);
		}

		const lifecycleErrors = routeLifecycleErrors([...consoleErrors, ...pageErrors]);
		console.log(JSON.stringify({ consoleErrors, pageErrors, lifecycleErrors }, null, 2));
		expect(lifecycleErrors, 'invalid inspection selection lifecycle errors').toEqual([]);
	});

	test('loads Leases without the route-load runtime error and keeps an offline property filter in-app', async ({ page }) => {
		const consoleErrors: string[] = [];
		const pageErrors: string[] = [];
		page.on('console', (message) => {
			if (message.type() === 'error') consoleErrors.push(message.text());
		});
		page.on('pageerror', (error) => pageErrors.push(error.stack ?? String(error)));

		await login(page);
		await page.goto('/leases', { waitUntil: 'domcontentloaded' });
		await expect(page.getByTestId('leases-page')).toBeVisible({ timeout: 15_000 });
		await expect(page.getByTestId('leases-header')).toBeVisible({ timeout: 15_000 });

		await page.goto('/properties', { waitUntil: 'domcontentloaded' });
		await expect(page.getByTestId('properties-page')).toBeVisible({ timeout: 15_000 });
		const propertySearch = page.getByRole('searchbox', { name: 'Search properties…' });
		await expect(propertySearch).toBeVisible({ timeout: 15_000 });

		// Keep the app shell connected while making the server request unavailable. Chromium's
		// context-wide offline toggle can navigate the Vite document to chrome-error:// when a
		// debounced query starts, which masks the shallow filter-navigation behavior under test.
		await page.route('**/api/v1/properties/page**', async (route) => route.abort('internetdisconnected'));
		try {
			await propertySearch.fill('offline-batch1');
			await expect(page).toHaveURL(/\/properties\?q=offline-batch1/, { timeout: 5_000 });
			await expect(page.getByTestId('properties-page')).toBeVisible();
		} finally {
			await page.unroute('**/api/v1/properties/page**');
		}

		const lifecycleErrors = routeLifecycleErrors([...consoleErrors, ...pageErrors]);
		console.log(JSON.stringify({ consoleErrors, pageErrors, lifecycleErrors }, null, 2));
		expect(lifecycleErrors, 'leases/offline filter lifecycle errors').toEqual([]);
	});

	test('recovers the dashboard after a failed request when Retry is clicked', async ({ page }) => {
		let retryClicked = false;
		let dashboardRequests = 0;

		await page.route('**/api/v1/portfolios/*/dashboard', async (route) => {
			dashboardRequests += 1;
			if (!retryClicked) {
				await fulfillJson(route, 503, { title: 'Injected dashboard outage' });
				return;
			}
			await route.continue();
		});

		await login(page);
		await expect(page.getByTestId('dashboard-error')).toBeVisible({ timeout: 15_000 });
		await page.getByTestId('dashboard-properties-link').click();
		await expect(page).toHaveURL(/\/properties$/);
		await page.goto('/');
		await expect(page.getByTestId('dashboard-error')).toBeVisible({ timeout: 15_000 });
		retryClicked = true;
		await page.getByTestId('dashboard-retry').click();
		await expect(page.getByTestId('dashboard-summary')).toBeVisible({ timeout: 15_000 });
		expect(dashboardRequests).toBeGreaterThan(1);
	});

	test('opens dashboard conversation links at the canonical detail route', async ({ page }) => {
		const id = 7401;
		const summary = conversationSummary(id, 'Dashboard deep-link conversation');
		const requestSequence: string[] = [];
		await page.route('**/api/v1/conversations/page**', (route) =>
			fulfillJson(route, 200, { items: [summary], totalCount: 1, skip: 0, take: 5 })
		);
		await stubConversationDetail(page, id, summary.subject, requestSequence);

		await login(page);
		await page.goto('/');
		const link = page.getByTestId(`dashboard-message-${id}`);
		await expect(link).toBeVisible({ timeout: 15_000 });
		await link.click();
		await expect(page).toHaveURL(new RegExp(`/messages/${id}$`));
		await expect(page.getByTestId('conversation-title')).toHaveText(summary.subject);
		expect(requestSequence, 'dashboard conversation request sequence').toEqual(['detail GET', 'mark-read']);
	});

	test('opens notice conversation links at the canonical detail route', async ({ page }) => {
		const id = 7402;
		const subject = 'Notice deep-link conversation';
		const requestSequence: string[] = [];
		await page.route('**/api/v1/notices*', (route) =>
			fulfillJson(route, 200, [
				{
					id: 7402,
					leaseManagementId: 1,
					tenantAccountId: 1,
					recipientTenantId: 1,
					tenantName: 'Runtime Tenant',
					propertyName: 'Runtime Property',
					unitNumber: '1',
					noticeType: 'rent-reminder',
					status: 'Approved',
					subject,
					body: 'A notice opened from a conversation.',
					reason: 'Runtime deep-link check',
					triggerDate: '2026-08-09',
					conversationId: id,
					approvedChannels: 'Portal',
					createdAt: '2026-08-09T16:00:00Z',
					updatedAt: '2026-08-09T16:00:00Z'
				}
			])
		);
		await stubConversationDetail(page, id, subject, requestSequence);

		await login(page);
		await page.goto('/notices');
		await page.getByRole('button', { name: 'Review draft' }).click();
		const link = page.getByTestId('notice-conversation-link');
		await expect(link).toBeVisible({ timeout: 15_000 });
		await link.click();
		await expect(page).toHaveURL(new RegExp(`/messages/${id}$`));
		await expect(page.getByTestId('conversation-title')).toHaveText(subject);
		expect(requestSequence, 'notice conversation request sequence').toEqual(['detail GET', 'mark-read']);
	});

	test('fetches a missing conversation once, before any mark-read request', async ({ page }) => {
		const id = 7403;
		let detailRequests = 0;
		let markReadRequests = 0;
		await page.route(`**/api/v1/conversations/${id}`, async (route) => {
			detailRequests += 1;
			await fulfillJson(route, 404, { title: 'Conversation not found' });
		});
		await page.route(`**/api/v1/conversations/${id}/read`, async (route) => {
			markReadRequests += 1;
			await fulfillJson(route, 500, { title: 'mark-read must not run for a missing conversation' });
		});

		await login(page);
		await page.goto(`/messages/${id}`);
		await expect(page.getByTestId('conversation-error')).toBeVisible({ timeout: 15_000 });
		await page.waitForTimeout(500);
		expect(detailRequests, 'permanent 404 query should not retry').toBe(1);
		expect(markReadRequests, 'missing detail must never issue mark-read').toBe(0);
		await page.getByTestId('conversation-error-back').click();
		await expect(page).toHaveURL(/\/messages$/);
	});

	test('conversation error Retry recovers and Back returns to the list', async ({ page }) => {
		const retryId = 7404;
		const missingId = 7405;
		let retryClicked = false;
		const requestSequence: string[] = [];
		const successfulRequestSequence: string[] = [];
		await page.route(`**/api/v1/conversations/${retryId}`, async (route) => {
			if (!retryClicked) {
				requestSequence.push('detail GET (failed)');
				await fulfillJson(route, 503, { title: 'Injected conversation outage' });
				return;
			}
			requestSequence.push('detail GET');
			successfulRequestSequence.push('detail GET');
			await fulfillJson(route, 200, conversation(retryId, 'Recovered conversation'));
		});
		await page.route(`**/api/v1/conversations/${retryId}/read`, async (route) => {
			requestSequence.push('mark-read');
			successfulRequestSequence.push('mark-read');
			await route.fulfill({ status: 204 });
		});
		await page.route(`**/api/v1/conversations/${missingId}`, (route) =>
			fulfillJson(route, 404, { title: 'Conversation not found' })
		);

		await login(page);
		await page.goto(`/messages/${retryId}`);
		await expect(page.getByTestId('conversation-error')).toBeVisible({ timeout: 15_000 });
		retryClicked = true;
		await page.getByTestId('conversation-error-retry').click();
		await expect(page.getByTestId('conversation-title')).toHaveText('Recovered conversation', { timeout: 15_000 });
		expect(successfulRequestSequence, 'conversation Retry success request sequence').toEqual(['detail GET', 'mark-read']);
		expect(requestSequence.slice(-2), 'conversation Retry request sequence tail').toEqual(['detail GET', 'mark-read']);

		await page.goto(`/messages/${missingId}`);
		await expect(page.getByTestId('conversation-error')).toBeVisible({ timeout: 15_000 });
		await page.getByTestId('conversation-error-back').click();
		await expect(page).toHaveURL(/\/messages$/);
	});

	test('mobile conversation Back action returns to the conversation list', async ({ page }) => {
		const id = 7406;
		await page.setViewportSize({ width: 390, height: 844 });
		await page.route('**/api/v1/conversations/page**', (route) =>
			fulfillJson(route, 200, { items: [conversationSummary(id)], totalCount: 1, skip: 0, take: 20 })
		);
		await stubConversationDetail(page, id);

		await login(page);
		await page.goto(`/messages/${id}`);
		await expect(page.getByTestId('conversation-title')).toBeVisible({ timeout: 15_000 });
		await expect(page.getByTestId('conversation-back')).toBeVisible();
		await page.getByTestId('conversation-back').click();
		await expect(page).toHaveURL(/\/messages$/);
		await expect(page.getByTestId('conversation-list-pane')).toBeVisible();
		await expect(page.getByTestId('conversation-row')).toBeVisible({ timeout: 15_000 });
	});
});
