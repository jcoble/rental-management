import type { LeaseStatus } from '$lib/types';

export type LeaseDeleteTarget = {
	leaseNumber?: string | null;
	status?: LeaseStatus | null;
	propertyName?: string | null;
	unitNumber?: string | null;
};

export type LeaseDeleteState = {
	title: string;
	message: string;
	confirmLabel: string;
};

function labelLease(lease: LeaseDeleteTarget | null | undefined) {
	return lease?.leaseNumber?.trim() || 'this lease';
}

function labelHome(lease: LeaseDeleteTarget | null | undefined) {
	const parts = [lease?.propertyName?.trim(), lease?.unitNumber?.trim() ? `Unit ${lease.unitNumber.trim()}` : '']
		.filter(Boolean);
	return parts.length > 0 ? ` for ${parts.join(' - ')}` : '';
}

export function getLeaseDeleteState(lease: LeaseDeleteTarget | null | undefined): LeaseDeleteState {
	const leaseLabel = labelLease(lease);
	const homeLabel = labelHome(lease);

	switch (lease?.status) {
		case 'Active':
			return {
				title: 'Remove active lease',
				message: `${leaseLabel} is active${homeLabel}. Removing it will terminate this lease record, release the unit from active occupancy, and hide it from active lease workflows. Use Create / Send notice for a normal move-out; remove only duplicate or mistaken leases.`,
				confirmLabel: 'Remove active lease',
			};
		case 'NoticeGiven':
			return {
				title: 'Remove notice lease',
				message: `${leaseLabel} already has notice recorded${homeLabel}. Removing it will terminate this lease record and hide it from move-out follow-up. Continue only if this lease was created by mistake.`,
				confirmLabel: 'Remove lease',
			};
		case 'PendingSignature':
			return {
				title: 'Delete pending lease',
				message: `${leaseLabel} is pending signature. Deleting it will remove this lease from signing and rent workflows. Continue only if the pending agreement should be discarded.`,
				confirmLabel: 'Delete pending lease',
			};
		case 'Draft':
			return {
				title: 'Delete draft lease',
				message: `Delete draft lease ${leaseLabel}? This cannot be undone.`,
				confirmLabel: 'Delete draft',
			};
		default:
			return {
				title: 'Delete lease',
				message: `Delete lease ${leaseLabel}? This cannot be undone.`,
				confirmLabel: 'Delete',
			};
	}
}
