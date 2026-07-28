import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import type { NotificationNavigationIntent } from '../api/types/notification.ts';
import type { SelectedAccessContextSummary } from '../types/user.ts';
import { notificationIntentUrl } from '../utils/portalLinks.ts';

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
		assert.match(source, /notificationIntentUrl\(item\.navigationIntent/);
		assertOccursBefore(
			source,
			'await notificationStore.markAsRead(item.id);',
			'notificationIntentUrl(item.navigationIntent'
		);
		assert.doesNotMatch(source, /actionUrl/);
	});

	it('marks notifications read when a tenant opens them from the dashboard summary', () => {
		const source = readFileSync(
			new URL('../../routes/(portal)/portal/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(source, /function openDashboardNotification\(item: NotificationItem\)/);
		assert.match(source, /notificationStore\.markAsRead\(item\.id\)/);
		assert.match(source, /notificationStore\.refresh\(\)/);
		assert.match(source, /notificationIntentUrl\(item\.navigationIntent/);
		assertOccursBefore(
			source,
			'await notificationStore.markAsRead(item.id);',
			'notificationIntentUrl(item.navigationIntent'
		);
		assertOccursBefore(
			source,
			"queryClient.invalidateQueries({ queryKey: ['notifications-unread-count'] })",
			'notificationIntentUrl(item.navigationIntent'
		);
		assert.doesNotMatch(source, /actionUrl/);
	});
});

const tenantAuthority: SelectedAccessContextSummary = {
	accessContextId: 31,
	portfolioId: 9,
	workspaceName: 'Tenant home',
	accessRevision: 7,
	activeExperience: 'Tenant'
};

const managementAuthority: SelectedAccessContextSummary = {
	accessContextId: 32,
	portfolioId: 9,
	workspaceName: 'Manager workspace',
	accessRevision: 3,
	activeExperience: 'Management'
};

function tenantMessageIntent(
	overrides: Partial<NotificationNavigationIntent> = {}
): NotificationNavigationIntent {
	return {
		experience: 'Tenant',
		destination: 'Message',
		accessContextId: 31,
		accessRevision: 7,
		resource: { kind: 'Conversation', id: 42 },
		parentResource: null,
		childResource: null,
		action: 'Open',
		expiresAtUtc: '2099-01-01T00:00:00Z',
		fallbackDestination: 'Home',
		...overrides
	};
}

function tenantLedgerIntent(
	overrides: Partial<NotificationNavigationIntent> = {}
): NotificationNavigationIntent {
	return {
		...tenantMessageIntent({
			destination: 'TenantLedgerEntry',
			resource: { kind: 'TenantLedgerEntry', id: 872 },
			parentResource: { kind: 'TenantAccount', id: 8 }
		}),
		...overrides
	};
}

describe('notification intent URL mapping', () => {
	it('maps a current typed tenant message intent without accepting a raw URL', () => {
		assert.equal(
			notificationIntentUrl(tenantMessageIntent(), tenantAuthority, Date.UTC(2026, 0, 1)),
			'/portal/messages?conversation=42'
		);
		assert.equal(
			notificationIntentUrl(
				{ ...tenantMessageIntent(), actionUrl: 'https://evil.example' } as never,
				tenantAuthority,
				Date.UTC(2026, 0, 1)
			),
			'/portal'
		);
	});

	it('routes tenant ledger entry intents to scoped portal payments with account and entry focus', () => {
		assert.equal(
			notificationIntentUrl(tenantLedgerIntent(), tenantAuthority, Date.UTC(2026, 0, 27)),
			'/portal/payments?account=8&entry=872'
		);
		assert.equal(
			notificationIntentUrl(
				tenantLedgerIntent({ parentResource: { kind: 'Unit', id: 8 } }),
				tenantAuthority,
				Date.UTC(2026, 0, 27)
			),
			'/portal'
		);
		assert.equal(
			notificationIntentUrl(
				tenantLedgerIntent({ resource: { kind: 'Payment', id: 872 } }),
				tenantAuthority,
				Date.UTC(2026, 0, 27)
			),
			'/portal'
		);
	});

	it('keeps management ledger entry intents on the staff detail route', () => {
		assert.equal(
			notificationIntentUrl(
				tenantLedgerIntent({
					experience: 'Management',
					accessContextId: 32,
					accessRevision: 3
				}),
				managementAuthority,
				Date.UTC(2026, 0, 27)
			),
			'/tenant-accounts/8/entries/872'
		);
	});

	it('falls back internally for expired, revised, cross-context, and malformed intents', () => {
		const now = Date.UTC(2026, 0, 2);
		assert.equal(
			notificationIntentUrl(
				tenantMessageIntent({ expiresAtUtc: '2026-01-01T00:00:00Z' }),
				tenantAuthority,
				now
			),
			'/portal'
		);
		assert.equal(
			notificationIntentUrl(tenantMessageIntent({ accessRevision: 6 }), tenantAuthority, now),
			'/portal'
		);
		assert.equal(
			notificationIntentUrl(tenantMessageIntent({ accessContextId: 99 }), tenantAuthority, now),
			'/portal'
		);
		assert.equal(
			notificationIntentUrl(
				tenantMessageIntent({ destination: 'https://evil.example' as never }),
				tenantAuthority,
				now
			),
			'/portal'
		);
		assert.equal(
			notificationIntentUrl(
				tenantMessageIntent({ resource: { kind: 'WorkOrder', id: 42 } }),
				tenantAuthority,
				now
			),
			'/portal'
		);
	});
});
