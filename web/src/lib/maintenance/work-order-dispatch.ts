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
