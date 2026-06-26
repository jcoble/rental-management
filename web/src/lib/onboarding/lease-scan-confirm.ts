export interface OnboardingLeaseConfirmInput {
	leaseNumber: string;
	propertyId: number;
	unitId: number;
	tenantId: number;
	startDate: string;
	endDate: string;
	monthlyRent: number;
	securityDeposit: number;
	lateFeeAmount: number;
	rentDueDay: number;
	rentTrackingStartMode?: string;
	rentTrackingStartDate?: string | null;
}

export function buildOnboardingLeaseScanOverrides(input: OnboardingLeaseConfirmInput): string {
	return JSON.stringify({
		propertyId: input.propertyId,
		unitId: input.unitId,
		tenantId: input.tenantId,
		leaseNumber: input.leaseNumber,
		startDate: input.startDate,
		endDate: input.endDate,
		monthlyRent: input.monthlyRent,
		securityDeposit: input.securityDeposit,
		lateFee: input.lateFeeAmount,
		rentDueDay: input.rentDueDay,
		rentTrackingStartMode: input.rentTrackingStartMode,
		rentTrackingStartDate: input.rentTrackingStartDate
	});
}
