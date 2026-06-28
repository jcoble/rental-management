import type { EsignStatus, LeaseStatus } from '$lib/types';

const SIGNABLE_LEASE_STATUSES = new Set<LeaseStatus>(['Draft', 'PendingSignature', 'Active']);

interface SignatureSendState {
	esignStatus?: EsignStatus | null;
	leaseStatus?: LeaseStatus | null;
	hasSignedDocument?: boolean | null;
}

export function canSendLeaseForSignature(
	leaseStatus?: LeaseStatus | null,
	esignStatus?: EsignStatus | null,
	hasSignedDocument = false
): boolean {
	if (esignStatus === 'Signed') return false;
	if (hasSignedDocument) return false;
	if (!leaseStatus) return false;
	return SIGNABLE_LEASE_STATUSES.has(leaseStatus);
}

export function canShowLeaseSignatureSendAction(
	leaseStatus?: LeaseStatus | null,
	signatureStatus?: SignatureSendState | null
): boolean {
	return canSendLeaseForSignature(
		visibleLeaseStatus(leaseStatus, signatureStatus),
		signatureStatus?.esignStatus,
		signatureStatus?.hasSignedDocument === true
	);
}

export function leaseSignatureQueuedMessage(tenantName?: string | null): string {
	const recipient = tenantName?.trim() || 'the tenant';
	return `Signing request queued for ${recipient}. Watch the email queue below for delivery status.`;
}

export function hasAgreementAfterSignatureSend(currentHasDocument: boolean, result?: SignatureSendState | null): boolean {
	return currentHasDocument || result?.esignStatus === 'Sent';
}

export function visibleLeaseStatus(
	leaseStatus?: LeaseStatus | null,
	signatureStatus?: SignatureSendState | null
): LeaseStatus | undefined {
	if (!signatureStatus?.leaseStatus) return leaseStatus ?? undefined;
	if (!leaseStatus) return signatureStatus.leaseStatus;
	if (signatureStatus.leaseStatus === leaseStatus) return leaseStatus;
	if (leaseStatus === 'Draft' && signatureStatus.leaseStatus === 'PendingSignature') return 'PendingSignature';
	if (leaseStatus === 'PendingSignature' && signatureStatus.leaseStatus === 'Active') return 'Active';
	return leaseStatus;
}

export function signableStateMessage(leaseStatus?: LeaseStatus | null): string {
	if (leaseStatus === 'NoticeGiven') {
		return 'This lease has notice given. Signature sending is only available before the lease is active.';
	}
	if (leaseStatus === 'Expired' || leaseStatus === 'Terminated') {
		return 'This lease is closed. Signature sending is only available before the lease is active.';
	}
	return 'Signature sending is available for draft, pending-signature, or active unsigned leases.';
}
