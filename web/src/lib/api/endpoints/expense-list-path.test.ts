import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { buildExpenseListPagePath } from './expense-list-path.ts';

describe('buildExpenseListPagePath', () => {
	it('serializes work-order receipt filtering as an ASP.NET boolean', () => {
		assert.equal(
			buildExpenseListPagePath(2, {
				unitId: 3,
				take: 20,
				sort: '-incurredAt',
				workOrderLinkedOnly: true,
				incurredFrom: '2026-02-01',
				incurredTo: '2026-02-28',
				dueFrom: '2026-03-01',
				dueTo: '2026-03-15',
				paidFrom: '2026-03-05',
				paidTo: '2026-03-20'
			}),
			'/expenses/page?take=20&sort=-incurredAt&portfolioId=2&unitId=3&workOrderLinkedOnly=true&incurredFrom=2026-02-01&incurredTo=2026-02-28&dueFrom=2026-03-01&dueTo=2026-03-15&paidFrom=2026-03-05&paidTo=2026-03-20'
		);
	});

	it('omits optional filters when they are not set', () => {
		assert.equal(
			buildExpenseListPagePath(2, { take: 20 }),
			'/expenses/page?take=20&portfolioId=2'
		);
	});
});
