import { test, expect, type Page } from '@playwright/test';
import { login } from './helpers';

type UnitCase = {
	name: string;
	path: string;
	expectation: 'unit' | 'applications' | 'money' | 'error';
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
	}
];

function isExpectedTransportNoise(message: string): boolean {
	return /SignalR|Failed to fetch|Failed to complete negotiation|WebSocket connection|INTERNET_DISCONNECTED/.test(message);
}

function routeLifecycleErrors(messages: string[]): string[] {
	return messages.filter(
		(message) =>
			!isExpectedTransportNoise(message)
			&& /TypeError|\$set|Cannot read properties of undefined/.test(message),
	);
}

async function assertUnitEntry(page: Page, unitCase: UnitCase, phase: string): Promise<void> {
	await expect(page.getByTestId('unit-page'), `${unitCase.name} ${phase} shell`).toBeVisible({ timeout: 15_000 });

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
		case 'error':
			await expect(page.getByTestId('unit-error'), `${unitCase.name} ${phase} error`).toBeVisible({ timeout: 15_000 });
			break;
	}
}

test.describe('Batch 1 route recovery at the browser lifecycle boundary', () => {
	test('cold-loads and refreshes valid, encoded, unknown, tab, and money unit URLs', async ({ page }) => {
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

		await page.context().setOffline(true);
		try {
			await propertySearch.fill('offline-batch1');
			await expect(page).toHaveURL(/\/properties\?q=offline-batch1/, { timeout: 5_000 });
			await expect(page.getByTestId('properties-page')).toBeVisible();
		} finally {
			await page.context().setOffline(false);
		}

		const lifecycleErrors = routeLifecycleErrors([...consoleErrors, ...pageErrors]);
		console.log(JSON.stringify({ consoleErrors, pageErrors, lifecycleErrors }, null, 2));
		expect(lifecycleErrors, 'leases/offline filter lifecycle errors').toEqual([]);
	});
});
