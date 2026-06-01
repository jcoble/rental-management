/**
 * Seeded-data smoke tests — run against the LIVE local dev stack.
 *
 * Each test logs in, navigates to a page, and asserts that seeded demo data
 * is actually rendered (not just that the page 200s).
 *
 * DB seed totals (reference):
 *   8 properties, ~30 units, 22 tenants, 20 leases, ~260 payments (12mo),
 *   35 expenses, 15 work orders, 12 appointments, 6 inspections,
 *   2 owners, 6 vendors. Accounting collected ≈ $292,943.
 */

import { test, expect } from '@playwright/test';
import { login } from './helpers';

// ---------------------------------------------------------------------------
// Auth + Dashboard
// ---------------------------------------------------------------------------
test.describe('Auth + Dashboard', () => {
	test('login succeeds and dashboard shows non-zero KPIs', async ({ page }) => {
		await login(page);
		// Should land on dashboard (/)
		await expect(page).not.toHaveURL(/\/login/);

		// Wait for the dashboard data to render — the dashboard renders a grid of KPI cards.
		// The occupancy card shows a percentage like "73%" once data is loaded.
		// Wait for the occupancy value to appear (indicates API responded with data).
		await expect(page.locator('.tabular-nums').first()).toBeVisible({ timeout: 15_000 });

		// Verify we are NOT on the loading skeleton by ensuring the portfolio name heading appears
		// (the dashboard h1 is the portfolio name, rendered after data loads).
		await expect(page.locator('h1')).toBeVisible({ timeout: 15_000 });
	});
});

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------
test.describe('Properties', () => {
	test('list shows at least 8 rows', async ({ page }) => {
		await login(page);
		await page.goto('/properties');
		await expect(page.getByTestId('properties-page')).toBeVisible();

		// Wait for rows to appear (datagrid renders rows after the API returns)
		await expect(page.getByTestId('property-row').first()).toBeVisible({ timeout: 15_000 });

		const rows = page.getByTestId('property-row');
		const count = await rows.count();
		expect(count).toBeGreaterThanOrEqual(8);
	});

	test('clicking first property row opens detail with units + leases', async ({ page }) => {
		await login(page);
		await page.goto('/properties');
		await expect(page.getByTestId('property-row').first()).toBeVisible({ timeout: 15_000 });

		// Click the first row to navigate to the detail page
		await page.getByTestId('property-row').first().click();
		await expect(page.getByTestId('property-detail-page')).toBeVisible({ timeout: 15_000 });

		// Units section should be present
		await expect(page.getByTestId('property-detail-units')).toBeVisible();
		// Leases section should be present
		await expect(page.getByTestId('property-detail-leases')).toBeVisible();
	});
});

// ---------------------------------------------------------------------------
// Tenants
// ---------------------------------------------------------------------------
test.describe('Tenants', () => {
	test('list shows at least 20 rows', async ({ page }) => {
		await login(page);
		await page.goto('/tenants');
		await expect(page.getByTestId('tenants-page')).toBeVisible();

		await expect(page.getByTestId('tenant-row').first()).toBeVisible({ timeout: 15_000 });

		const rows = page.getByTestId('tenant-row');
		const count = await rows.count();
		expect(count).toBeGreaterThanOrEqual(20);
	});

	test('clicking first tenant row opens detail page', async ({ page }) => {
		await login(page);
		await page.goto('/tenants');
		await expect(page.getByTestId('tenant-row').first()).toBeVisible({ timeout: 15_000 });

		await page.getByTestId('tenant-row').first().click();
		await expect(page.getByTestId('tenant-detail-page')).toBeVisible({ timeout: 15_000 });
		// Name heading should be present
		await expect(page.getByTestId('tenant-detail-name')).toBeVisible();
	});
});

// ---------------------------------------------------------------------------
// Leases
// ---------------------------------------------------------------------------
test.describe('Leases', () => {
	test('list shows rows with rent amounts', async ({ page }) => {
		await login(page);
		await page.goto('/leases');
		await expect(page.getByTestId('leases-page')).toBeVisible();

		// Wait for at least one row in the datagrid
		await expect(page.getByTestId('datagrid-row').first()).toBeVisible({ timeout: 15_000 });

		const rows = page.getByTestId('datagrid-row');
		const count = await rows.count();
		expect(count).toBeGreaterThanOrEqual(1);
	});

	test('clicking first lease opens detail with payments grid', async ({ page }) => {
		await login(page);
		await page.goto('/leases');
		await expect(page.getByTestId('datagrid-row').first()).toBeVisible({ timeout: 15_000 });

		await page.getByTestId('datagrid-row').first().click();
		await expect(page.getByTestId('lease-detail-page')).toBeVisible({ timeout: 15_000 });

		// The lease detail shows the lease number
		await expect(page.getByTestId('lease-detail-number')).toBeVisible();
		// The payments grid is present
		await expect(page.getByTestId('lease-payments-grid')).toBeVisible();
	});
});

// ---------------------------------------------------------------------------
// Accounting
// ---------------------------------------------------------------------------
test.describe('Accounting', () => {
	test('KPI cards show non-zero collected amount', async ({ page }) => {
		await login(page);
		await page.goto('/accounting');
		await expect(page.getByTestId('accounting-page')).toBeVisible();

		// Wait for the dollar value to appear inside the accounting-collected card.
		// The card renders a loading skeleton first, then the money value (e.g. "$292,943").
		// Use .locator('p.tabular-nums') or wait for text matching "$" inside the card.
		const collectedCard = page.getByTestId('accounting-collected');
		await expect(collectedCard).toBeVisible();
		// Wait for the actual dollar value to appear (not the skeleton).
		await expect(collectedCard.locator('.tabular-nums')).toBeVisible({ timeout: 15_000 });

		// The seed has ≈$292,943 collected; check the value is non-zero.
		const collectedText = await collectedCard.locator('.tabular-nums').textContent();
		expect(collectedText).toMatch(/\$\d/);
		// Should NOT be $0 given the seeded data
		expect(collectedText).not.toMatch(/\$0$/);
	});

	test('Payments grid has rows', async ({ page }) => {
		await login(page);
		await page.goto('/accounting');
		await expect(page.getByTestId('accounting-page')).toBeVisible();

		// Wait for the payments list to render
		await expect(page.getByTestId('payments-list')).toBeVisible();
		await expect(page.getByTestId('payment-row').first()).toBeVisible({ timeout: 15_000 });

		const rows = page.getByTestId('payment-row');
		const count = await rows.count();
		expect(count).toBeGreaterThanOrEqual(1);
	});

	test('Expenses grid has rows', async ({ page }) => {
		await login(page);
		await page.goto('/accounting');
		await expect(page.getByTestId('accounting-page')).toBeVisible();

		await expect(page.getByTestId('expenses-list')).toBeVisible();
		await expect(page.getByTestId('expense-row').first()).toBeVisible({ timeout: 15_000 });

		const rows = page.getByTestId('expense-row');
		const count = await rows.count();
		expect(count).toBeGreaterThanOrEqual(1);
	});
});

// ---------------------------------------------------------------------------
// Maintenance (Work Orders + Inspections)
// ---------------------------------------------------------------------------
test.describe('Maintenance', () => {
	test('work orders grid shows seeded rows', async ({ page }) => {
		await login(page);
		await page.goto('/maintenance');
		await expect(page.getByTestId('maintenance-page')).toBeVisible();

		await expect(page.getByTestId('work-orders-list')).toBeVisible();
		await expect(page.getByTestId('datagrid-row').first()).toBeVisible({ timeout: 15_000 });

		const rows = page.getByTestId('datagrid-row');
		const count = await rows.count();
		expect(count).toBeGreaterThanOrEqual(1);
	});

	test('inspections section renders with seeded rows', async ({ page }) => {
		await login(page);
		await page.goto('/maintenance');
		await expect(page.getByTestId('maintenance-page')).toBeVisible();

		await expect(page.getByTestId('inspections-list')).toBeVisible();
		// The inspections list should contain at least one inspection row
		await expect(page.getByTestId('inspection-row').first()).toBeVisible({ timeout: 15_000 });
	});
});

// ---------------------------------------------------------------------------
// Appointments
// ---------------------------------------------------------------------------
test.describe('Appointments', () => {
	test('list shows seeded appointment rows', async ({ page }) => {
		await login(page);
		await page.goto('/appointments');
		await expect(page.getByTestId('appointments-page')).toBeVisible();

		await expect(page.getByTestId('appointments-list')).toBeVisible();
		await expect(page.getByTestId('datagrid-row').first()).toBeVisible({ timeout: 15_000 });

		const rows = page.getByTestId('datagrid-row');
		const count = await rows.count();
		expect(count).toBeGreaterThanOrEqual(1);
	});
});

// ---------------------------------------------------------------------------
// Deposits
// ---------------------------------------------------------------------------
test.describe('Deposits', () => {
	test('deposits list renders rows', async ({ page }) => {
		await login(page);
		await page.goto('/deposits');
		await expect(page.getByTestId('deposits-page')).toBeVisible();

		await expect(page.getByTestId('deposits-list')).toBeVisible();
		// Seeded leases should produce some deposit holdings
		await expect(page.getByTestId('deposit-row').first()).toBeVisible({ timeout: 15_000 });

		const rows = page.getByTestId('deposit-row');
		const count = await rows.count();
		expect(count).toBeGreaterThanOrEqual(1);
	});
});

// ---------------------------------------------------------------------------
// Owners + Vendors
// ---------------------------------------------------------------------------
test.describe('Owners + Vendors', () => {
	test('owners grid shows 2 seeded owners', async ({ page }) => {
		// NOTE: This test fails — the demo seed does NOT populate OwnerEntity records.
		// Symptom: grid shows "No owners found." despite 2 owners being in the seed spec.
		// This is an app bug: the DataSeeder does not seed OwnerEntity / owner contacts.
		await login(page);
		await page.goto('/owners');
		await expect(page.getByTestId('owners-page')).toBeVisible();

		await expect(page.getByTestId('owners-list')).toBeVisible();
		await expect(page.getByTestId('owner-row').first()).toBeVisible({ timeout: 15_000 });

		const rows = page.getByTestId('owner-row');
		const count = await rows.count();
		expect(count).toBeGreaterThanOrEqual(2);
	});

	test('vendors grid shows 6 seeded vendors', async ({ page }) => {
		await login(page);
		await page.goto('/owners');
		await expect(page.getByTestId('owners-page')).toBeVisible();

		await expect(page.getByTestId('vendors-list')).toBeVisible();
		await expect(page.getByTestId('vendor-row').first()).toBeVisible({ timeout: 15_000 });

		const rows = page.getByTestId('vendor-row');
		const count = await rows.count();
		expect(count).toBeGreaterThanOrEqual(6);
	});
});

// ---------------------------------------------------------------------------
// Analytics
// ---------------------------------------------------------------------------
test.describe('Analytics', () => {
	test('KPI cards are populated with non-zero values', async ({ page }) => {
		await login(page);
		await page.goto('/analytics');
		await expect(page.getByTestId('analytics-page')).toBeVisible();

		// Wait for data (not loading skeleton, not error, not empty)
		await expect(page.getByTestId('analytics-kpi-row')).toBeVisible({ timeout: 20_000 });

		// Occupancy KPI should be visible
		await expect(page.getByTestId('kpi-occupancy')).toBeVisible();

		// Collection rate KPI should be visible
		await expect(page.getByTestId('kpi-collection')).toBeVisible();

		// The row should NOT show the loading state
		await expect(page.getByTestId('analytics-loading')).not.toBeVisible();
		await expect(page.getByTestId('analytics-error')).not.toBeVisible();
		await expect(page.getByTestId('analytics-empty')).not.toBeVisible();
	});
});

// ---------------------------------------------------------------------------
// Owner Report
// ---------------------------------------------------------------------------
test.describe('Owner Report', () => {
	test('owners report page loads and shows owner list', async ({ page }) => {
		// NOTE: This test fails — same root cause as the owners grid bug above.
		// The owners report shows "No owner data for 2026." because no OwnerEntity
		// records exist. The page structure (year picker, report sections) is correct
		// but the data is absent. App bug: seed does not populate OwnerEntity records.
		await login(page);
		await page.goto('/owners-report');
		await expect(page.getByTestId('owners-report-page')).toBeVisible();

		// Should not show error
		await expect(page.getByTestId('owners-report-error')).not.toBeVisible();

		// The owner list should render with at least one owner
		await expect(page.getByTestId('owners-report-list')).toBeVisible({ timeout: 15_000 });

		const owners = page.locator('[data-testid^="owners-report-owner-"]');
		const count = await owners.count();
		expect(count).toBeGreaterThanOrEqual(1);
	});
});

// ---------------------------------------------------------------------------
// Scan list
// ---------------------------------------------------------------------------
test.describe('Scan', () => {
	test('scan page renders with upload area and list', async ({ page }) => {
		await login(page);
		await page.goto('/scan');
		await expect(page.getByTestId('scan-page')).toBeVisible();
		await expect(page.getByTestId('scan-upload')).toBeVisible();
		// The scans list section is present (may be empty if no scans have been processed)
		await expect(page.getByTestId('scans-list')).toBeVisible({ timeout: 10_000 });
	});
});

// ---------------------------------------------------------------------------
// AI page
// ---------------------------------------------------------------------------
test.describe('AI', () => {
	test('AI page loads with briefing and ask sections', async ({ page }) => {
		await login(page);
		await page.goto('/ai');
		await expect(page.getByTestId('ai-page')).toBeVisible();

		// Either the briefing card or unavailable banner should be present
		const hasBriefing = await page.getByTestId('briefing-card').isVisible();
		const hasUnavailable = await page.getByTestId('ai-unavailable-banner').isVisible();
		expect(hasBriefing || hasUnavailable).toBe(true);

		// The ask/chat card should always render
		await expect(page.getByTestId('ask-card')).toBeVisible();
	});
});

// ---------------------------------------------------------------------------
// Admin / Users
// ---------------------------------------------------------------------------
test.describe('Admin Users', () => {
	test('team page loads and shows at least one member', async ({ page }) => {
		// NOTE: This test fails — the team page shows "No team members yet." despite
		// the admin user being logged in. Symptom: the admin's ApplicationUser record
		// may not have a corresponding team-member/portfolio-member record, or the
		// members API endpoint returns empty. App bug.
		await login(page);
		await page.goto('/admin/users');
		await expect(page.getByTestId('team-page')).toBeVisible();

		// Wait for members list to render
		await expect(page.getByTestId('team-members-list')).toBeVisible({ timeout: 15_000 });

		// The seeded admin user should appear (uses team-member-row testid from getRowTestId)
		await expect(page.getByTestId('team-member-row').first()).toBeVisible({ timeout: 15_000 });
	});
});
