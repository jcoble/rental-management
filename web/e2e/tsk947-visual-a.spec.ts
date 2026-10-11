import { expect, test, type Page } from '@playwright/test';
import { login } from './helpers';

/** Open a sidebar group unless the item we want to look at is already showing. */
async function openNavGroup(page: Page, groupId: string, probeTestId: string) {
	if (await page.getByTestId(probeTestId).isVisible()) return;
	await page.getByTestId(`nav-group-${groupId}`).click();
	await expect(page.getByTestId(probeTestId)).toBeVisible();
}

/** The sidebar links currently marked as the page you are on. */
function highlightedLinks(page: Page) {
	return page.getByTestId('main-nav').locator('a[aria-current="page"]');
}

/**
 * TSK-947 Lane A — one highlighted sidebar link, a real page name in the top bar,
 * and plain names for the notices and settings entries.
 */
test.describe('Sidebar highlight and names', () => {
	test('highlights only the page you are on', async ({ page }) => {
		await login(page);

		await page.goto('/accounting/past-due', { waitUntil: 'domcontentloaded' });
		await openNavGroup(page, 'money', 'nav-accounting-past-due');
		await expect(highlightedLinks(page)).toHaveCount(1);
		await expect(highlightedLinks(page)).toContainText("Who's behind");

		await page.goto('/settings/lease-templates', { waitUntil: 'domcontentloaded' });
		await openNavGroup(page, 'settings', 'nav-settings-lease-templates');
		await expect(highlightedLinks(page)).toHaveCount(1);
		await expect(highlightedLinks(page)).toContainText('Lease settings');
	});

	test('names the units list in the top bar', async ({ page }) => {
		await login(page);

		await page.goto('/units', { waitUntil: 'domcontentloaded' });
		await expect(page.getByTestId('page-title')).toHaveText('Units');
	});

	test('uses plain names for notices and general settings', async ({ page }) => {
		await login(page);

		// Landing on the page opens the group it belongs to, so the link is on screen.
		await page.goto('/notices', { waitUntil: 'domcontentloaded' });
		await openNavGroup(page, 'inbox', 'nav-notices');
		await expect(page.getByTestId('nav-notices')).toContainText('Tenant notices');

		// The sidebar is live by now, so the Settings group opens on a click.
		await openNavGroup(page, 'settings', 'nav-settings');
		await expect(page.getByTestId('nav-settings')).toContainText('General');
	});
});
