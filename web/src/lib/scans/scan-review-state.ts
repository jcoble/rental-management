import { recordHref } from '../navigation/record-href.ts';

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

export function createdRecordHref(
	type: string | null | undefined,
	id: number | null | undefined,
	unitId?: number | null
): string {
	if (!type || !id) return '/accounting';
	if (type === 'Payment') return recordHref('payment', { id, unitId });
	if (type === 'WorkOrder') return recordHref('workOrder', { id, unitId });
	if (type === 'Lease') return recordHref('lease', { id, unitId });
	if (type === 'Application' || type === 'RentalApplication') return recordHref('application', { id, unitId });
	// A Loan has no standalone detail page; the fallback takes the user back to the accounting hub.
	// The scan confirmation screen may navigate with richer context when it has the property id.
	if (type === 'Loan') return '/accounting';
	return recordHref('expense', { id, unitId });
}
