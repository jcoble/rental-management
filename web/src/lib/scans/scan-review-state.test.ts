import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { createdRecordArticle, isTerminalScanReview, shouldDisableScanReviewControls } from './scan-review-state.ts';

describe('scan review terminal state', () => {
	it('treats confirmed, rejected, and freshly confirmed scans as terminal', () => {
		assert.equal(isTerminalScanReview('Confirmed', false), true);
		assert.equal(isTerminalScanReview('Rejected', false), true);
		assert.equal(isTerminalScanReview('Reviewing', true), true);
		assert.equal(isTerminalScanReview('Reviewing', false), false);
	});

	it('disables review controls while processing or after terminal states', () => {
		assert.equal(shouldDisableScanReviewControls('Processing', false), true);
		assert.equal(shouldDisableScanReviewControls('Pending', false), true);
		assert.equal(shouldDisableScanReviewControls('Confirmed', false), true);
		assert.equal(shouldDisableScanReviewControls('Reviewing', true), true);
		assert.equal(shouldDisableScanReviewControls('Reviewing', false), false);
	});

	it('uses the right article for created record labels', () => {
		assert.equal(createdRecordArticle('Expense'), 'an');
		assert.equal(createdRecordArticle('Application'), 'an');
		assert.equal(createdRecordArticle('Payment'), 'a');
		assert.equal(createdRecordArticle('Work Order'), 'a');
	});
});
