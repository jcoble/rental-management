import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { getTenantNoticeEmptyCopy, getTenantNoticeEmptyState } from './tenant-notice-state.ts';

describe('tenant notice state', () => {
	it('uses due-notice copy before a landlord forces a specific notice type', () => {
		assert.deepEqual(getTenantNoticeEmptyCopy(), {
			message: 'No notices are due for this tenant right now.',
			description: 'Renewal, late-rent, and move-out notices appear here automatically when they come due.',
		});
	});

	it('explains when a forced notice cannot be created for this tenant', () => {
		assert.deepEqual(getTenantNoticeEmptyCopy('Lease renewal offer'), {
			message: 'No lease renewal offer could be created.',
			description: 'This tenant needs an active eligible lease for that notice type.',
		});
	});

	it('hides forced notice actions until the tenant has an active lease', () => {
		assert.deepEqual(
			getTenantNoticeEmptyState({
				activeLeaseCount: 0,
				tenantId: 42,
			}),
			{
				message: 'No notice can be created yet.',
				description:
					'Create or activate a lease for this tenant before sending renewal or move-out notices.',
				showForceControls: false,
				leaseActionHref: '/leases?create=1&tenantId=42',
				leaseActionLabel: 'Create lease',
			}
		);
	});

	it('does not keep offering forced notice actions after a forced attempt creates no draft', () => {
		assert.deepEqual(
			getTenantNoticeEmptyState({
				activeLeaseCount: 1,
				forcedNoticeLabel: 'Lease renewal offer',
				tenantId: 42,
			}),
			{
				message: 'No lease renewal offer could be created.',
				description: 'This tenant needs an active eligible lease for that notice type.',
				showForceControls: false,
			}
		);
	});
});
