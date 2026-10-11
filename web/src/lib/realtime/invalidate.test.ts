import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { invalidateQueriesForDataUpdate } from './invalidate-keys.ts';

function createQueryClientSpy() {
	const invalidated: unknown[][] = [];
	const removed: unknown[][] = [];
	return {
		client: {
			invalidateQueries: ({ queryKey }: { queryKey: unknown[] }) => invalidated.push(queryKey),
			removeQueries: ({ queryKey }: { queryKey: unknown[] }) => removed.push(queryKey)
		},
		invalidated,
		removed
	};
}

describe('invalidateQueriesForDataUpdate', () => {
	it('refreshes property detail aggregates when a unit update includes its owning property', () => {
		const { client, invalidated, removed } = createQueryClientSpy();

		invalidateQueriesForDataUpdate(client as never, 'EntityUpdated', {
			entityType: 'Unit',
			entityId: 42,
			data: { id: 42, propertyId: 7, unitNumber: '2A' },
			timestamp: '2026-06-23T00:00:00Z'
		});

		assert.deepEqual(removed, []);
		assert.deepEqual(invalidated, [
			['units'],
			['units-for-lease'],
			['properties'],
			['dashboard'],
			['unit-dashboard'],
			['unit-timeline'],
			['property', 7]
		]);
	});

	it('does not guess a property detail key when a unit delete payload lacks property context', () => {
		const { client, invalidated, removed } = createQueryClientSpy();

		invalidateQueriesForDataUpdate(client as never, 'EntityDeleted', {
			entityType: 'Unit',
			entityId: 42,
			timestamp: '2026-06-23T00:00:00Z'
		});

		assert.deepEqual(removed, [['unit', 42]]);
		assert.equal(invalidated.some((key) => key[0] === 'property'), false);
	});

	it('refreshes conversation lists, open details, tenant portal messages, and staff header unread count', () => {
		const { client, invalidated, removed } = createQueryClientSpy();

		invalidateQueriesForDataUpdate(client as never, 'EntityUpdated', {
			entityType: 'Conversation',
			entityId: 9,
			timestamp: '2026-06-24T01:00:00Z'
		});

		assert.deepEqual(removed, []);
		assert.deepEqual(invalidated, [
			['conversations'],
			['conversation'],
			['portal-conversations'],
			['portal-conversation'],
			['header-unread-messages']
		]);
	});

	it('refreshes notification lists, unread counts, and the tenant portal notification page', () => {
		const { client, invalidated, removed } = createQueryClientSpy();

		invalidateQueriesForDataUpdate(client as never, 'EntityUpdated', {
			entityType: 'Notification',
			entityId: 17,
			timestamp: '2026-07-24T19:42:44Z'
		});

		assert.deepEqual(removed, []);
		assert.deepEqual(invalidated, [
			['notifications'],
			['notifications-unread-count'],
			['portal-notifications-page']
		]);
	});
});
