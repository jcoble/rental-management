import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { buildLoanListPagePath, buildLoanListPath } from './loan-list-path.ts';

describe('buildLoanListPath', () => {
	it('serializes server-side list controls for property loans', () => {
		assert.equal(
			buildLoanListPagePath({
				propertyId: 4,
				skip: 10,
				take: 10,
				sort: '-startDate',
				from: '2026-01-01',
				to: '2026-12-31'
			}),
			'/loans/page?skip=10&take=10&sort=-startDate&from=2026-01-01&to=2026-12-31&propertyId=4'
		);
	});

	it('keeps the legacy list endpoint available', () => {
		assert.equal(buildLoanListPath({ propertyId: 4, take: 20 }), '/loans?take=20&propertyId=4');
	});
});
