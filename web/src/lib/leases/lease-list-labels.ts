import { formatStatusLabel } from '../utils/status-labels.ts';

const AGREEMENT_STATUS_LABELS: Record<string, string> = {
	Draft: 'Draft lease',
	AwaitingSignatures: 'Waiting for signatures',
	Issued: 'Waiting for signatures',
	Prepared: 'Waiting for signatures',
	PartiallySigned: 'Waiting for signatures',
	ExecutionPending: 'Waiting for signatures',
	Upcoming: 'Signed lease starts soon',
	Active: 'Signed lease',
	Executed: 'Signed lease',
	Expired: 'Lease ended',
	Superseded: 'Replaced lease',
	Void: 'Voided lease',
	Canceled: 'Canceled draft',
	MissingEvidence: 'Lease record needs review'
};

const LEASE_LIFECYCLE_LABELS: Record<string, string> = {
	Preparing: 'Preparing move-in',
	Upcoming: 'Starting soon',
	Occupied: 'Occupied',
	Ending: 'Ending soon',
	AccountingCloseout: 'Closing out',
	Closed: 'Closed',
	Canceled: 'Canceled'
};

const AGREEMENT_CHANGE_TYPE_LABELS: Record<string, string> = {
	Correction: 'Fix a typo',
	Restatement: 'Rewrite the whole lease',
	Renewal: 'Renew it',
	MonthToMonth: 'Switch to month-to-month',
	ReissueAsAddendum: 'Add a page'
};

export function leaseAgreementStatusLabel(status: string | null | undefined): string {
	if (!status) return 'No signed lease yet';
	return AGREEMENT_STATUS_LABELS[status] ?? formatStatusLabel(status);
}

export function leaseLifecycleLabel(status: string): string {
	return LEASE_LIFECYCLE_LABELS[status] ?? formatStatusLabel(status);
}

export function leaseAgreementChangeTypeLabel(changeType: string | null | undefined): string {
	if (!changeType) return 'Lease change';
	return AGREEMENT_CHANGE_TYPE_LABELS[changeType] ?? formatStatusLabel(changeType);
}
