export const SCAN_HISTORY_FILTERS = ['All', 'Pending', 'Reviewing', 'Confirmed', 'Rejected', 'Failed'] as const;

export type ScanHistoryFilter = (typeof SCAN_HISTORY_FILTERS)[number];

export function resolveScanHistoryFilter(value: string | null | undefined): ScanHistoryFilter {
	if (SCAN_HISTORY_FILTERS.includes(value as ScanHistoryFilter)) {
		return value as ScanHistoryFilter;
	}

	return 'All';
}

export function formatScanHistoryEmptyMessage(filter: ScanHistoryFilter): string {
	switch (filter) {
		case 'Pending':
			return 'No processing scans.';
		case 'Reviewing':
			return 'No scans ready to review.';
		case 'Confirmed':
			return 'No confirmed scans.';
		case 'Rejected':
			return 'No rejected scans.';
		case 'Failed':
			return 'No failed scans.';
		case 'All':
		default:
			return 'No scan drafts found.';
	}
}
