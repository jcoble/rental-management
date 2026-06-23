import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	hasNoticeMoveOutDate,
	scannedLeaseDocumentLinkLabel,
	tabForLeaseEdit,
} from './lease-detail-state.ts';

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

describe('scannedLeaseDocumentLinkLabel', () => {
	it('does not assume every scanned lease source was uploaded as a PDF', () => {
		assert.equal(scannedLeaseDocumentLinkLabel(true), 'Open full size');
		assert.equal(scannedLeaseDocumentLinkLabel(false), 'View scanned document');
	});
});

describe('hasNoticeMoveOutDate', () => {
	it('requires a non-blank move-out date before giving notice', () => {
		assert.equal(hasNoticeMoveOutDate(''), false);
		assert.equal(hasNoticeMoveOutDate('   '), false);
		assert.equal(hasNoticeMoveOutDate(null), false);
		assert.equal(hasNoticeMoveOutDate('2026-07-15'), true);
	});
});
