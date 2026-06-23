import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { getTenantNoticeEmptyCopy } from './tenant-notice-state.ts';

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
});
