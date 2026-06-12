import { test, expect } from '@playwright/test';
import { login } from './helpers';

/**
 * IA Wave 1 guard: every sidebar nav destination must resolve to a real page (HTTP 200,
 * not a 404/500). The nav tree was regrouped (Money/Rentals/Work/Inbox + Settings hub),
 * labels changed, and several routes became redirects — this is the cheap "nothing in the
 * sidebar points at a dead page" check the review's appendix asked for. We navigate by URL
 * (the sidebar's own hrefs) since the e2e suite already drives by goto().
 */

// The admin-visible nav hrefs after the Wave 1 regroup. Redirect targets (e.g. /tax,
// /owners-report) are still reachable pages; old URLs that became redirects are covered
// by the redirect tests below.
const NAV_HREFS = [
	'/', // Dashboard (pinned)
	'/scan', // Scan / Add (pinned)
	'/accounting', // Money
	'/reports', // Reports
	'/properties',
	'/tenants',
	'/leases',
	'/applications',
	'/maintenance',
	'/appointments',
	'/vendors', // split out of /owners
	'/messages',
	'/notices', // "Tenant notices"
	'/ai', // Assistant (bottom rail)
	'/docs', // Help (bottom rail)
	'/settings', // Settings hub
	'/admin/users', // Team
	'/owners', // Owners (Settings)
	'/audit' // Activity history (Settings)
];

// Old URLs that became redirects in Wave 1 → where they should land.
const REDIRECTS: Array<[string, RegExp]> = [
	['/analytics', /\/$/], // Insights merged into Dashboard
	['/maintenance/work-orders/1', /\/maintenance\/1$/] // WO detail dedupe
];

test.describe('IA Wave 1 — nav resolves', () => {
	test.beforeEach(async ({ page }) => {
		await login(page);
	});

	for (const href of NAV_HREFS) {
		test(`nav destination ${href} returns a non-error page`, async ({ page }) => {
			const response = await page.goto(href);
			// A real response with a 2xx/3xx status — never a 404/500.
			expect(response, `no response for ${href}`).not.toBeNull();
			expect(response!.status(), `${href} status`).toBeLessThan(400);
			// We must not have been bounced to login (would mean a broken guard, not a real page).
			await expect(page).not.toHaveURL(/\/login/);
		});
	}

	for (const [from, toPattern] of REDIRECTS) {
		test(`legacy URL ${from} redirects to its canonical page`, async ({ page }) => {
			await page.goto(from);
			await expect(page).toHaveURL(toPattern);
		});
	}
});
