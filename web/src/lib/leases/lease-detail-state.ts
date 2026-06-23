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
