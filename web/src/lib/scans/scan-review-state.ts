export function isTerminalScanReview(status: string | null | undefined, hasInSessionConfirmedRecord: boolean): boolean {
	return status === 'Rejected' || status === 'Confirmed' || hasInSessionConfirmedRecord;
}

export function shouldDisableScanReviewControls(status: string | null | undefined, hasInSessionConfirmedRecord: boolean): boolean {
	return status === 'Pending' || status === 'Processing' || isTerminalScanReview(status, hasInSessionConfirmedRecord);
}

export function createdRecordArticle(label: string): 'a' | 'an' {
	return label === 'Application' || label === 'Expense' ? 'an' : 'a';
}

export function createdRecordLabel(type: string | null | undefined): string {
	switch (type) {
		case 'Payment': return 'Payment';
		case 'WorkOrder': return 'Work Order';
		case 'Lease': return 'Lease';
		case 'Application': return 'Application';
		case 'Expense': return 'Expense';
		case 'Loan': return 'Loan';
		default: return 'Record';
	}
}

export function createdRecordHref(type: string | null | undefined, id: number | null | undefined): string {
	if (!type || !id) return '/accounting';
	if (type === 'Payment') return `/accounting/payments/${id}`;
	if (type === 'WorkOrder') return `/maintenance/${id}`;
	if (type === 'Lease') return `/leases/${id}`;
	if (type === 'Application') return `/applications/${id}`;
	// A Loan has no standalone detail page — it lives under its property — so the confirm flow navigates
	// to the property instead. There is no per-id loan route to link to here.
	if (type === 'Loan') return '/accounting';
	return `/accounting/expenses/${id}`;
}
