import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
	createdRecordArticle,
	createdRecordHref,
	createdRecordLabel,
	isTerminalScanReview,
	shouldDisableScanReviewControls,
} from './scan-review-state.ts';

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

	it('links confirmed application scans to the application record', () => {
		assert.equal(createdRecordLabel('Application'), 'Application');
		assert.equal(createdRecordHref('Application', 123), '/applications/123');
		assert.equal(createdRecordHref('Payment', 45, { tenantAccountId: 7 }), '/tenant-accounts/7/entries/45');
		assert.equal(createdRecordHref('WorkOrder', 46), '/maintenance/46');
		assert.equal(createdRecordHref('LeaseAgreement', 47, { leaseManagementId: 71 }), '/leases/71');
		assert.equal(createdRecordHref('LeaseAgreement', 47), '/leases');
		assert.equal(createdRecordHref('Expense', 48), '/accounting/expenses/48');
		assert.equal(createdRecordHref(null, 48), '/accounting');
		assert.equal(createdRecordHref('Application', null), '/accounting');
	});

	it('links unit-tied confirmed scans to the unit command center tab', () => {
		assert.equal(
			createdRecordHref('Application', 123, { unitId: 9 }),
			'/units/9?tab=leasing&view=applications&app=123',
		);
		assert.equal(
			createdRecordHref('RentalApplication', 123, { unitId: 9 }),
			'/units/9?tab=leasing&view=applications&app=123',
		);
		assert.equal(createdRecordHref('Payment', 45, { unitId: 9 }), '/units/9?tab=money&ledger=rent&payment=45');
		assert.equal(createdRecordHref('WorkOrder', 46, { unitId: 9 }), '/units/9?tab=maintenance&view=work-orders&wo=46');
		assert.equal(
			createdRecordHref('LeaseAgreement', 47, {
				unitId: 9,
				leaseManagementId: 71,
			}),
			'/units/9?tab=tenant-lease&view=agreements&leaseManagement=71',
		);
		assert.equal(createdRecordHref('LeaseAgreement', 47, { unitId: 9 }), '/units/9?tab=tenant-lease&view=agreements');
		assert.equal(createdRecordHref('Expense', 48, { unitId: 9 }), '/units/9?tab=money&ledger=expenses&expense=48');
	});
});
