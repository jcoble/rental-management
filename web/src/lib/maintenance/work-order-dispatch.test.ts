import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import * as dispatch from './work-order-dispatch.ts';

const {
	canDispatchToVendor,
	dispatchVendorBlockReason,
	workOrderStatusActionTargets,
} = dispatch;

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

	it('formats status transition copy with user-facing status labels', () => {
		assert.equal(typeof dispatch.formatStatusTransitionCopy, 'function');
		assert.equal(
			dispatch.formatStatusTransitionCopy('InProgress', 'WaitingParts'),
			'Change status from In progress to Waiting on parts.'
		);
	});

	it('matches the mobile work-order status action targets', () => {
		assert.deepEqual(workOrderStatusActionTargets('New'), [
			'Scheduled',
			'InProgress',
			'WaitingParts',
			'Completed',
			'Cancelled'
		]);
		assert.deepEqual(workOrderStatusActionTargets('Completed'), [
			'New',
			'Scheduled',
			'InProgress',
			'WaitingParts',
			'Cancelled'
		]);
		const newTargets: readonly string[] = workOrderStatusActionTargets('New');
		assert.equal(newTargets.includes('OnHold'), false);
		assert.equal(newTargets.includes('Archived'), false);
	});

	it('hides active dispatch hint copy once the work order is terminal', () => {
		assert.equal(typeof dispatch.shouldShowActiveDispatchHint, 'function');
		assert.equal(dispatch.shouldShowActiveDispatchHint('InProgress', true, false), true);
		assert.equal(dispatch.shouldShowActiveDispatchHint('Completed', true, true), false);
		assert.equal(dispatch.shouldShowActiveDispatchHint('Cancelled', true, true), false);
	});
});
