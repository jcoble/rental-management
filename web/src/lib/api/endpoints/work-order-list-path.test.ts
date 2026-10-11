import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { buildWorkOrderListPagePath, buildWorkOrderListPath } from './work-order-list-path.ts';

describe('work-order list paths', () => {
	it('serializes status, open-only, and date-window filters', () => {
		assert.equal(
			buildWorkOrderListPagePath(2, {
				propertyId: 3,
				unitId: 4,
				vendorId: 5,
				status: 'Scheduled',
				priority: 'High',
				openOnly: true,
				take: 20,
				sort: '-requestedAt',
				requestedFrom: '2026-04-01',
				requestedTo: '2026-04-30',
				scheduledFrom: '2026-05-01',
				scheduledTo: '2026-05-15',
				completedFrom: '2026-06-01',
				completedTo: '2026-06-15'
			}),
			'/work-orders/page?take=20&sort=-requestedAt&portfolioId=2&propertyId=3&unitId=4&vendorId=5&status=Scheduled&priority=High&openOnly=true&requestedFrom=2026-04-01&requestedTo=2026-04-30&scheduledFrom=2026-05-01&scheduledTo=2026-05-15&completedFrom=2026-06-01&completedTo=2026-06-15'
		);
	});

	it('omits optional filters when they are not set', () => {
		assert.equal(buildWorkOrderListPath(2, { take: 20 }), '/work-orders?take=20&portfolioId=2');
	});
});
