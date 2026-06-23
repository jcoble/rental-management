import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	canSendLeaseForSignature,
	hasAgreementAfterSignatureSend,
	signableStateMessage,
	visibleLeaseStatus,
} from './lease-esign.ts';

describe('lease e-sign state helpers', () => {
	it('allows draft and pending-signature leases to be sent', () => {
		assert.equal(canSendLeaseForSignature('Draft', 'None'), true);
		assert.equal(canSendLeaseForSignature('PendingSignature', 'Sent'), true);
		assert.equal(canSendLeaseForSignature('PendingSignature', 'Declined'), true);
	});

	it('blocks signed, active, and notice-given leases', () => {
		assert.equal(canSendLeaseForSignature('Draft', 'Signed'), false);
		assert.equal(canSendLeaseForSignature('Active', 'None'), false);
		assert.equal(canSendLeaseForSignature('NoticeGiven', 'None'), false);
		assert.equal(canSendLeaseForSignature('Expired', 'None'), false);
		assert.equal(canSendLeaseForSignature('Terminated', 'None'), false);
	});

	it('explains why active and notice-given leases cannot be sent', () => {
		assert.match(signableStateMessage('Active'), /already active/);
		assert.match(signableStateMessage('NoticeGiven'), /notice given/);
	});

	it('treats a successful signature send as proof the agreement exists', () => {
		assert.equal(hasAgreementAfterSignatureSend(false, { esignStatus: 'Sent', leaseStatus: 'PendingSignature' }), true);
		assert.equal(hasAgreementAfterSignatureSend(true, { esignStatus: 'Sent', leaseStatus: 'PendingSignature' }), true);
		assert.equal(hasAgreementAfterSignatureSend(false, { esignStatus: 'None', leaseStatus: 'Draft' }), false);
	});

	it('uses fresh signature status while the lease detail cache catches up', () => {
		assert.equal(
			visibleLeaseStatus('PendingSignature', { esignStatus: 'Signed', leaseStatus: 'Active' }),
			'Active'
		);
		assert.equal(visibleLeaseStatus('PendingSignature', undefined), 'PendingSignature');
		assert.equal(visibleLeaseStatus(undefined, { esignStatus: 'Sent', leaseStatus: 'PendingSignature' }), 'PendingSignature');
	});
});
