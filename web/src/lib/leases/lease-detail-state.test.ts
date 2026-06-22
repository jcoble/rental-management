import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { tabForLeaseEdit } from './lease-detail-state.ts';

describe('lease detail edit state', () => {
	it('keeps the overview tab when editing starts from overview', () => {
		assert.equal(tabForLeaseEdit('overview'), 'overview');
	});

	it('moves to overview when editing starts from a non-editable tab', () => {
		assert.equal(tabForLeaseEdit('history'), 'overview');
		assert.equal(tabForLeaseEdit('ledger'), 'overview');
		assert.equal(tabForLeaseEdit('agreement'), 'overview');
	});
});
