import { formatStatusLabel } from '../utils/status-labels.ts';

export interface DispatchVendorContact {
	phone?: string | null;
}

export const USER_ASSIGNABLE_WORK_ORDER_STATUSES = [
	'New',
	'Scheduled',
	'InProgress',
	'WaitingParts',
	'Completed',
	'Cancelled'
] as const;

export type UserAssignableWorkOrderStatus = (typeof USER_ASSIGNABLE_WORK_ORDER_STATUSES)[number];

export function canDispatchToVendor(vendor: DispatchVendorContact | null | undefined): boolean {
	return Boolean(vendor?.phone?.trim());
}

export function dispatchVendorBlockReason(vendor: DispatchVendorContact | null | undefined): string {
	if (!vendor) return 'Select a vendor to text this job.';
	if (!canDispatchToVendor(vendor)) return 'Add a phone number before texting this vendor the job.';
	return '';
}

export function formatStatusTransitionCopy(
	currentStatus: string | null | undefined,
	nextStatus: string | null | undefined
): string {
	return `Change status from ${formatStatusLabel(currentStatus)} to ${formatStatusLabel(nextStatus)}.`;
}

export function workOrderStatusActionTargets(
	currentStatus: string | null | undefined
): UserAssignableWorkOrderStatus[] {
	return USER_ASSIGNABLE_WORK_ORDER_STATUSES.filter((status) => status !== currentStatus);
}

// The "{Vendor} has the job — they'll text back DONE" banner asserts a real, in-flight dispatch, so it
// must be driven by actual dispatch state, NOT by merely assigning a vendor in the form. Show it only
// when the work order has an OPEN VendorDispatch (`hasActiveDispatch`, computed server-side) or one was
// just dispatched in this session (`wasJustDispatched`), and the order isn't already terminal.
export function shouldShowActiveDispatchHint(
	status: string | null | undefined,
	hasActiveDispatch: boolean,
	wasJustDispatched: boolean
): boolean {
	if (status === 'Completed' || status === 'Cancelled') return false;
	return Boolean(wasJustDispatched || hasActiveDispatch);
}
