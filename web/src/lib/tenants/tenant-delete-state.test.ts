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

	it('uses server delete-blocked copy for active leases when provided', () => {
		assert.deepEqual(
			getTenantDeleteState({
				fullName: 'Maya Ortiz',
				activeLeaseCount: 1,
				deleteBlockedReason: 'This tenant has an active or notice-given lease; end or reassign it first.',
			}),
			{
				message: 'This tenant has an active or notice-given lease; end or reassign it first.',
				confirmDisabled: true,
			}
		);
	});

	it('blocks tenants with lease history even without active leases', () => {
		assert.deepEqual(
			getTenantDeleteState({ fullName: 'Jordan Lee', activeLeaseCount: 0, leaseHistoryCount: 2 }),
			{
				message: 'Jordan Lee has lease history. Keep the tenant record to preserve past leases and payments.',
				confirmDisabled: true,
			}
		);
	});

	it('blocks generic server-declared non-deletable tenants', () => {
		assert.deepEqual(
			getTenantDeleteState({ fullName: 'Riley Chen', activeLeaseCount: 0, leaseHistoryCount: 0, canDelete: false }),
			{
				message: 'Riley Chen cannot be deleted right now.',
				confirmDisabled: true,
			}
		);
	});
});
