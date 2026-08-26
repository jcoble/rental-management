import { expect, test } from '@playwright/test';
import { login } from './helpers';

/**
 * TSK-947 W9. Notification settings are one page with sections instead of three pages, the
 * Settings rail has a single Notifications entry, and a message reply just sends — the delivery
 * choices hide behind a "Delivery" button.
 */
test.describe('one notifications page and a reply that just sends', () => {
	test('the notifications page shows my alerts and tenant notices as sections', async ({
		page
	}) => {
		await login(page);
		await page.goto('/settings/notifications', { waitUntil: 'domcontentloaded' });

		await expect(page.getByTestId('notifications-my-alerts')).toBeVisible();
		await expect(page.getByTestId('notifications-tenant-notices')).toBeVisible();

		// The old pages are gone; their links land on the matching section.
		await page.goto('/settings/notifications/tenant-notices', { waitUntil: 'domcontentloaded' });
		await expect(page).toHaveURL(/\/settings\/notifications(#tenant-notices)?$/);
		await expect(page.getByTestId('notifications-tenant-notices')).toBeVisible();
	});

	test('the Settings rail has one Notifications entry', async ({ page }) => {
		await login(page);
		await page.goto('/', { waitUntil: 'domcontentloaded' });

		const settingsGroup = page.getByTestId('nav-group-settings');
		await expect(settingsGroup).toBeVisible();
		if (!(await page.getByTestId('nav-settings-notifications').isVisible())) {
			await settingsGroup.click();
		}

		await expect(page.getByTestId('nav-settings-notifications')).toBeVisible();
		await expect(page.getByTestId('nav-settings-notifications')).toContainText('Notifications');
		await expect(page.getByTestId('nav-settings-notifications-tenant-notices')).toHaveCount(0);
		await expect(page.getByTestId('nav-settings-notifications-team-routing')).toHaveCount(0);
		await expect(page.getByTestId('nav-settings-notifications-my-alerts')).toHaveCount(0);
	});

	test('a reply shows one delivery line and hides the channel boxes', async ({ page }) => {
		await login(page);
		await page.goto('/messages', { waitUntil: 'domcontentloaded' });

		const rows = page.getByTestId('conversation-row');
		test.skip((await rows.count()) === 0, 'No conversation exists in this sample data.');
		await rows.first().click();

		await expect(page.getByTestId('reply-input')).toBeVisible();
		await expect(page.getByTestId('reply-delivery-line')).toBeVisible();
		await expect(page.getByTestId('reply-channel-portal')).toHaveCount(0);
		await expect(page.getByTestId('reply-channel-email')).toHaveCount(0);
		await expect(page.getByTestId('reply-channel-sms')).toHaveCount(0);

		await page.getByTestId('reply-delivery-menu').click();
		await expect(page.getByTestId('reply-channel-portal')).toBeVisible();
		await expect(page.getByTestId('reply-channel-email')).toBeVisible();
		await expect(page.getByTestId('reply-channel-sms')).toBeVisible();
	});

	test('a reply sends without touching the delivery choices', async ({ page }) => {
		await login(page);
		await page.goto('/messages', { waitUntil: 'domcontentloaded' });

		const rows = page.getByTestId('conversation-row');
		test.skip((await rows.count()) === 0, 'No conversation exists in this sample data.');
		await rows.first().click();

		const reply = page.getByTestId('reply-input');
		await expect(reply).toBeVisible();
		await reply.fill('Thanks — I will take a look today.');
		await page.getByTestId('reply-send').click();

		await expect(reply).toHaveValue('');
		await expect(
			page.getByTestId('conversation-messages').getByText('Thanks — I will take a look today.')
		).toBeVisible();
	});
});
