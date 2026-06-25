import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

function assertOccursBefore(source: string, before: string, after: string) {
	const beforeIndex = source.indexOf(before);
	const afterIndex = source.indexOf(after);

	assert.notEqual(beforeIndex, -1, `Expected to find: ${before}`);
	assert.notEqual(afterIndex, -1, `Expected to find: ${after}`);
	assert.ok(beforeIndex < afterIndex, `Expected "${before}" before "${after}"`);
}

describe('portal notifications page', () => {
	it('marks notifications read when a tenant opens them from the full page', () => {
		const source = readFileSync(
			new URL('../../routes/(portal)/portal/notifications/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(source, /notificationStore\.markAsRead\(item\.id\)/);
		assert.match(source, /notificationStore\.refresh\(\)/);
		assert.match(source, /goto\(portalActionUrl\(item\.actionUrl\)/);
		assertOccursBefore(
			source,
			'await notificationStore.markAsRead(item.id);',
			'await goto(portalActionUrl(item.actionUrl), { invalidateAll: true });'
		);
		assert.doesNotMatch(source, /<a\s+href=\{portalActionUrl\(item\.actionUrl\)\}/);
	});

	it('marks notifications read when a tenant opens them from the dashboard summary', () => {
		const source = readFileSync(
			new URL('../../routes/(portal)/portal/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(source, /function openDashboardNotification\(item: NotificationItem\)/);
		assert.match(source, /notificationStore\.markAsRead\(item\.id\)/);
		assert.match(source, /notificationStore\.refresh\(\)/);
		assert.match(source, /goto\(portalActionUrl\(item\.actionUrl\)/);
		assertOccursBefore(
			source,
			'await notificationStore.markAsRead(item.id);',
			'await goto(portalActionUrl(item.actionUrl), { invalidateAll: true });'
		);
		assertOccursBefore(
			source,
			"queryClient.invalidateQueries({ queryKey: ['notifications-unread-count'] })",
			'await goto(portalActionUrl(item.actionUrl), { invalidateAll: true });'
		);
		assert.doesNotMatch(source, /<a\s+href=\{portalActionUrl\(item\.actionUrl\)\}/);
	});
});
