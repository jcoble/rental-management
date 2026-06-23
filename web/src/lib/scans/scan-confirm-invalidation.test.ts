import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { invalidateQueriesAfterScanConfirm } from './scan-confirm-invalidation.ts';

function createQueryClientSpy() {
	const invalidated: unknown[][] = [];
	return {
		client: {
			invalidateQueries: ({ queryKey }: { queryKey: unknown[] }) => invalidated.push(queryKey)
		},
		invalidated
	};
}

describe('invalidateQueriesAfterScanConfirm', () => {
	it('refreshes lookup caches that were empty before a scanned lease created records', () => {
		const { client, invalidated } = createQueryClientSpy();

		invalidateQueriesAfterScanConfirm(client, 'Lease');

		assert.deepEqual(invalidated, [
			['scans'],
			['leases'],
			['properties'],
			['tenants'],
			['units'],
			['units-for-lease'],
			['dashboard']
		]);
	});
});
