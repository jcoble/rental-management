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

		invalidateQueriesAfterScanConfirm(client, 'LeaseAgreement');

		assert.deepEqual(invalidated, [
			['scans'],
			['lease-managements'],
			['properties'],
			['tenants'],
			['units'],
			['units-for-lease'],
			['dashboard']
		]);
	});

	it('refreshes the loans and properties caches after a scanned mortgage created a loan', () => {
		const { client, invalidated } = createQueryClientSpy();

		invalidateQueriesAfterScanConfirm(client, 'Loan');

		assert.deepEqual(invalidated, [['scans'], ['loans'], ['properties'], ['dashboard']]);
	});
});
