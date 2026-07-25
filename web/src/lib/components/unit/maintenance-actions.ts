import type { ScanContext } from '$lib/scan/scan-context';

export const UNIT_MAINTENANCE_SUBJECTS = [
	{ view: 'work-orders', label: 'Work orders' },
	{ view: 'inspections', label: 'Inspections' },
	{ view: 'recurring', label: 'Recurring maintenance' },
	{ view: 'turnover', label: 'Turnover/make-ready' },
] as const;

export type UnitMaintenanceView = (typeof UNIT_MAINTENANCE_SUBJECTS)[number]['view'];

export function unitMaintenanceReturnTo(
	unitId: number | string,
	view: UnitMaintenanceView = 'work-orders',
): string {
	return `/units/${unitId}?tab=maintenance&view=${view}`;
}

export function workOrderReceiptScanContext(unitId: number | string, workOrderId?: number): Partial<ScanContext> {
	return {
		type: 'Expense',
		workOrderId,
		returnTo: unitMaintenanceReturnTo(unitId),
	};
}

export function defaultWorkOrderReceiptScanContext(
	unitId: number | string,
	workOrderIds: number[],
): Partial<ScanContext> {
	return workOrderReceiptScanContext(unitId, workOrderIds.length === 1 ? workOrderIds[0] : undefined);
}
