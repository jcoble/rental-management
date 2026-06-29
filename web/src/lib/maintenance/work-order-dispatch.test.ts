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

	it('shows the active dispatch hint only on a real dispatch, never on mere vendor assignment', () => {
		assert.equal(typeof dispatch.shouldShowActiveDispatchHint, 'function');
		// An OPEN dispatch (server-computed hasActiveDispatch) → banner shows.
		assert.equal(dispatch.shouldShowActiveDispatchHint('InProgress', true, false), true);
		// Just dispatched this session, server not yet refetched → banner shows.
		assert.equal(dispatch.shouldShowActiveDispatchHint('New', false, true), true);
		// Vendor merely assigned (no open dispatch, none just sent) → NO banner (the BUG-2 case).
		assert.equal(dispatch.shouldShowActiveDispatchHint('InProgress', false, false), false);
		// Terminal work orders never show it, even with an open dispatch.
		assert.equal(dispatch.shouldShowActiveDispatchHint('Completed', true, true), false);
		assert.equal(dispatch.shouldShowActiveDispatchHint('Cancelled', true, true), false);
	});
});
