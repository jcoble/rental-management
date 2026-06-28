import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	canSendLeaseForSignature,
	canShowLeaseSignatureSendAction,
	hasAgreementAfterSignatureSend,
	leaseSignatureQueuedMessage,
	signableStateMessage,
	visibleLeaseStatus,
} from './lease-esign.ts';

describe('lease e-sign state helpers', () => {
	it('allows draft, pending-signature, and active unsigned leases to be sent', () => {
		assert.equal(canSendLeaseForSignature('Draft', 'None'), true);
		assert.equal(canSendLeaseForSignature('PendingSignature', 'Sent'), true);
		assert.equal(canSendLeaseForSignature('PendingSignature', 'Declined'), true);
		assert.equal(canSendLeaseForSignature('Active', 'None'), true);
		assert.equal(canSendLeaseForSignature('Active', 'Sent'), true);
		assert.equal(canSendLeaseForSignature('Active', 'Declined'), true);
	});

	it('blocks signed and closed leases', () => {
		assert.equal(canSendLeaseForSignature('Draft', 'Signed'), false);
		assert.equal(canSendLeaseForSignature('Active', 'Signed'), false);
		assert.equal(canSendLeaseForSignature('NoticeGiven', 'None'), false);
		assert.equal(canSendLeaseForSignature('Expired', 'None'), false);
		assert.equal(canSendLeaseForSignature('Terminated', 'None'), false);
	});

	it('explains why closed leases cannot be sent', () => {
		assert.match(signableStateMessage('NoticeGiven'), /notice given/);
		assert.match(signableStateMessage('Expired'), /closed/);
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
		assert.equal(
			visibleLeaseStatus('Draft', { esignStatus: 'Sent', leaseStatus: 'PendingSignature' }),
			'PendingSignature'
		);
		assert.equal(visibleLeaseStatus('PendingSignature', undefined), 'PendingSignature');
		assert.equal(visibleLeaseStatus(undefined, { esignStatus: 'Sent', leaseStatus: 'PendingSignature' }), 'PendingSignature');
	});

	it('does not let stale signature status override a newer explicit lease lifecycle state', () => {
		assert.equal(
			visibleLeaseStatus('NoticeGiven', { esignStatus: 'Signed', leaseStatus: 'Active' }),
			'NoticeGiven'
		);
		assert.equal(
			visibleLeaseStatus('Terminated', { esignStatus: 'Signed', leaseStatus: 'Active' }),
			'Terminated'
		);
	});

	it('keeps the send action available while signature status is still loading', () => {
		assert.equal(canShowLeaseSignatureSendAction('Active', undefined), true);
		assert.equal(canShowLeaseSignatureSendAction('Draft', undefined), true);
		assert.equal(canShowLeaseSignatureSendAction('Terminated', undefined), false);
	});

	it('does not call a queued signing request a delivered email', () => {
		assert.equal(
			leaseSignatureQueuedMessage('Leah Garcia'),
			'Signing request queued for Leah Garcia. Watch the email queue below for delivery status.'
		);
		assert.equal(
			leaseSignatureQueuedMessage(undefined),
			'Signing request queued for the tenant. Watch the email queue below for delivery status.'
		);
	});
});
