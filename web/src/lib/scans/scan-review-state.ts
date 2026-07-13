import { recordHref } from '../navigation/record-href.ts';

export type CreatedRecordLinkContext = {
	unitId?: number | null;
	tenantAccountId?: number | null;
	leaseManagementId?: number | null;
};

export function isTerminalScanReview(status: string | null | undefined, hasInSessionConfirmedRecord: boolean): boolean {
	return status === 'Rejected' || status === 'Confirmed' || hasInSessionConfirmedRecord;
}

export function shouldDisableScanReviewControls(
	status: string | null | undefined,
	hasInSessionConfirmedRecord: boolean
): boolean {
	return status === 'Pending' || status === 'Processing' || isTerminalScanReview(status, hasInSessionConfirmedRecord);
}

export function createdRecordArticle(label: string): 'a' | 'an' {
	return label === 'Application' || label === 'Expense' ? 'an' : 'a';
}

export function createdRecordLabel(type: string | null | undefined): string {
	switch (type) {
		case 'Payment':
			return 'Payment';
		case 'WorkOrder':
			return 'Work Order';
		case 'LeaseAgreement':
			return 'Lease Agreement';
		case 'Application':
			return 'Application';
		case 'Expense':
			return 'Expense';
		case 'Loan':
			return 'Loan';
		default:
			return 'Record';
	}
}

export function createdRecordHref(
	type: string | null | undefined,
	id: number | null | undefined,
	context: CreatedRecordLinkContext = {}
): string {
	if (!type || !id) return '/accounting';
	const { unitId, tenantAccountId, leaseManagementId } = context;
	if (type === 'Payment') return recordHref('payment', { id, unitId, tenantAccountId });
	if (type === 'WorkOrder') return recordHref('workOrder', { id, unitId });
	if (type === 'LeaseAgreement') {
		// A confirmed LeaseAgreement scan stores the agreement id as its created entity id, but
		// canonical lease routes are relationship-keyed. Never reinterpret the agreement id as a
		// LeaseManagement id. Existing-relationship scans retain that id in capture context; newly
		// created relationships still have an exact unit destination when the API resolved the unit.
		if (leaseManagementId && leaseManagementId > 0) {
			return recordHref('leaseManagement', { id: leaseManagementId, unitId });
		}
		return unitId && unitId > 0 ? `/units/${unitId}?tab=lease` : '/leases';
	}
	if (type === 'Application' || type === 'RentalApplication') return recordHref('application', { id, unitId });
	// A Loan has no standalone detail page; the fallback takes the user back to the accounting hub.
	// The scan confirmation screen may navigate with richer context when it has the property id.
	if (type === 'Loan') return '/accounting';
	return recordHref('expense', { id, unitId });
}
