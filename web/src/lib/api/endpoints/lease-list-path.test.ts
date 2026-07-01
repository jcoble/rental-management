import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { buildLeaseListPagePath, buildLeaseListPath } from './lease-list-path.ts';

describe('lease list paths', () => {
	it('serializes entity and date-window filters', () => {
		assert.equal(
			buildLeaseListPagePath(2, {
				tenantId: 3,
				propertyId: 4,
				unitId: 5,
				status: 'Active',
				take: 20,
				sort: '-startDate',
				startFrom: '2026-01-01',
				startTo: '2026-12-31',
				endFrom: '2027-01-01',
				endTo: '2027-12-31',
				activeOn: '2026-07-01',
				activeFrom: '2026-07-01',
				activeTo: '2026-07-31'
			}),
			'/leases/page?take=20&sort=-startDate&portfolioId=2&tenantId=3&propertyId=4&unitId=5&status=Active&startFrom=2026-01-01&startTo=2026-12-31&endFrom=2027-01-01&endTo=2027-12-31&activeOn=2026-07-01&activeFrom=2026-07-01&activeTo=2026-07-31'
		);
	});

	it('omits optional filters when they are not set', () => {
		assert.equal(buildLeaseListPath(2, { take: 20 }), '/leases?take=20&portfolioId=2');
	});
});
