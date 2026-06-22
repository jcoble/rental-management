import type { EsignStatus, LeaseStatus } from '$lib/types';

const SIGNABLE_LEASE_STATUSES = new Set<LeaseStatus>(['Draft', 'PendingSignature']);

export function canSendLeaseForSignature(leaseStatus?: LeaseStatus | null, esignStatus?: EsignStatus | null): boolean {
	if (esignStatus === 'Signed') return false;
	if (!leaseStatus) return false;
	return SIGNABLE_LEASE_STATUSES.has(leaseStatus);
}

export function signableStateMessage(leaseStatus?: LeaseStatus | null): string {
	if (leaseStatus === 'Active') {
		return 'This lease is already active. Create or send agreements before activating a lease.';
	}
	if (leaseStatus === 'NoticeGiven') {
		return 'This lease has notice given. Signature sending is only available before the lease is active.';
	}
	if (leaseStatus === 'Expired' || leaseStatus === 'Terminated') {
		return 'This lease is closed. Signature sending is only available before the lease is active.';
	}
	return 'Signature sending is available for draft or pending-signature leases.';
}
