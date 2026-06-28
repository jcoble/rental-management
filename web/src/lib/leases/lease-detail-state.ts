export const LEASE_DETAIL_TABS = ['overview', 'agreement', 'ledger', 'history'] as const;

export type LeaseDetailTab = (typeof LEASE_DETAIL_TABS)[number];

export function resolveLeaseDetailTab(value: string | undefined | null): LeaseDetailTab {
	return LEASE_DETAIL_TABS.includes(value as LeaseDetailTab) ? (value as LeaseDetailTab) : 'overview';
}

export function tabForLeaseEdit(_currentTab: string | undefined | null): string {
	return 'overview';
}

export function scannedLeaseDocumentLinkLabel(scanIsImage: boolean | null | undefined): string {
	return scanIsImage ? 'Open full size' : 'View scanned document';
}

export function hasNoticeMoveOutDate(value: string | null | undefined): boolean {
	return Boolean(value?.trim());
}

const LEASE_STATUS_TRANSITIONS_TO_ACTIVE = new Set(['Draft', 'PendingSignature', 'NoticeGiven']);

const LEASE_STATUS_EDIT_TARGETS: Record<string, Set<string>> = {
	Draft: new Set(['Draft', 'PendingSignature', 'Active', 'Void']),
	PendingSignature: new Set(['PendingSignature', 'Active', 'Draft', 'Void']),
	Active: new Set(['Active', 'NoticeGiven', 'Expired', 'Terminated']),
	NoticeGiven: new Set(['NoticeGiven', 'Active', 'Expired', 'Terminated']),
	Expired: new Set(['Expired']),
	Terminated: new Set(['Terminated']),
	Void: new Set(['Void']),
};

export function canSetLeaseActive(status: string | null | undefined): boolean {
	return Boolean(status && LEASE_STATUS_TRANSITIONS_TO_ACTIVE.has(status));
}

export function leaseStatusOptionsForCurrentStatus(
	currentStatus: string | null | undefined,
	editableStatuses: readonly string[]
): string[] {
	if (!currentStatus) return [...editableStatuses];

	const allowed = LEASE_STATUS_EDIT_TARGETS[currentStatus];
	if (!allowed) return [...editableStatuses];

	return editableStatuses.filter((status) => allowed.has(status));
}
