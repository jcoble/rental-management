import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { reportDetailsRequired, screeningAction } from './screening-mode.ts';

describe('screeningAction', () => {
	it('offers the connected-provider screening when a provider is connected', () => {
		assert.equal(
			screeningAction({ providerConnected: true, consent: true, status: 'Submitted' }),
			'integrated',
		);
		assert.equal(
			screeningAction({ providerConnected: true, consent: true, status: 'UnderReview' }),
			'integrated',
		);
	});

	it('falls back to recording a screening done elsewhere when no provider is connected', () => {
		assert.equal(
			screeningAction({ providerConnected: false, consent: true, status: 'Submitted' }),
			'external',
		);
		assert.equal(
			screeningAction({ providerConnected: null, consent: true, status: 'UnderReview' }),
			'external',
		);
	});

	it('offers nothing without consent or on a closed application', () => {
		assert.equal(screeningAction({ providerConnected: true, consent: false, status: 'Submitted' }), null);
		assert.equal(screeningAction({ providerConnected: false, consent: false, status: 'Submitted' }), null);
		assert.equal(screeningAction({ providerConnected: true, consent: true, status: 'Declined' }), null);
		assert.equal(screeningAction({ providerConnected: false, consent: true, status: 'Approved' }), null);
		assert.equal(screeningAction({ providerConnected: true, consent: null, status: 'Submitted' }), null);
	});
});

describe('reportDetailsRequired', () => {
	it('requires the report details only for a decline that used the report', () => {
		assert.equal(reportDetailsRequired({ decision: 'Decline', consumerReportUsed: true }), true);
	});

	it('does not require them for any other combination', () => {
		assert.equal(reportDetailsRequired({ decision: 'Decline', consumerReportUsed: false }), false);
		assert.equal(reportDetailsRequired({ decision: 'Accept', consumerReportUsed: true }), false);
		assert.equal(reportDetailsRequired({ decision: 'Conditional', consumerReportUsed: true }), false);
		assert.equal(reportDetailsRequired({ decision: null, consumerReportUsed: true }), false);
		assert.equal(reportDetailsRequired({ decision: null, consumerReportUsed: false }), false);
	});
});
