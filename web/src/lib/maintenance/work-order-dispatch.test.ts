import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { canDispatchToVendor, dispatchVendorBlockReason } from './work-order-dispatch.ts';

describe('work order vendor dispatch eligibility', () => {
	it('requires a selected vendor with a non-blank phone number', () => {
		assert.equal(canDispatchToVendor(null), false);
		assert.equal(canDispatchToVendor({ phone: '' }), false);
		assert.equal(canDispatchToVendor({ phone: '   ' }), false);
		assert.equal(canDispatchToVendor({ phone: '614-555-0199' }), true);
	});

	it('explains why dispatch is blocked', () => {
		assert.equal(dispatchVendorBlockReason(null), 'Select a vendor to text this job.');
		assert.equal(
			dispatchVendorBlockReason({ phone: null }),
			'Add a phone number before texting this vendor the job.'
		);
		assert.equal(dispatchVendorBlockReason({ phone: '614-555-0199' }), '');
	});
});
