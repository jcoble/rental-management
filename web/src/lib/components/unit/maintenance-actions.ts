import type { ScanContext } from '$lib/scan/scan-context';

export function unitMaintenanceReturnTo(unitId: number | string): string {
	return `/units/${unitId}?tab=maintenance&view=work-orders`;
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
