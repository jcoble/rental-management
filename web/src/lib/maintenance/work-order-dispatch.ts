import { formatStatusLabel } from '../utils/status-labels.ts';

export interface DispatchVendorContact {
	phone?: string | null;
}

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

export function shouldShowActiveDispatchHint(
	status: string | null | undefined,
	hasAssignedVendor: boolean,
	wasJustDispatched: boolean
): boolean {
	if (status === 'Completed' || status === 'Cancelled') return false;
	return Boolean(wasJustDispatched || hasAssignedVendor);
}
