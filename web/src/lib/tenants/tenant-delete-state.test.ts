import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { getTenantDeleteState } from './tenant-delete-state.ts';

describe('tenant delete state', () => {
	it('uses irreversible destructive copy when a tenant has no active leases', () => {
		assert.deepEqual(
			getTenantDeleteState({ fullName: 'Avery Ellis', firstName: 'Avery', lastName: 'Ellis', activeLeaseCount: 0 }),
			{
				message: 'Delete "Avery Ellis"? This cannot be undone.',
				confirmDisabled: false,
			}
		);
	});

	it('blocks delete confirmation while active leases depend on the tenant', () => {
		assert.deepEqual(
			getTenantDeleteState({ fullName: 'Maya Ortiz', firstName: 'Maya', lastName: 'Ortiz', activeLeaseCount: 1 }),
			{
				message: 'Maya Ortiz has 1 active lease. End or reassign the lease before deleting this tenant.',
				confirmDisabled: true,
			}
		);
	});
});
