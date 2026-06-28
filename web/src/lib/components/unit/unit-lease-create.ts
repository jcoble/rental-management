export type UnitLeaseCreateContext = {
	unit: {
		id: number;
		propertyId: number;
		unitNumber: string;
		marketRent: number;
	};
	propertyName: string;
};

export type UnitLeaseCreateForm = {
	leaseNumber: string;
	propertyId: string;
	unitId: string;
	tenantId: string;
	tenantIds: string[];
	startDate: string;
	endDate: string;
	monthlyRent: string;
	securityDeposit: string;
	lateFeeAmount: string;
	rentDueDay: string;
	rentTrackingStartMode: string;
	rentTrackingStartDate: string;
	status: string;
	notes: string;
};

export type SimpleTenantForm = {
	firstName: string;
	lastName: string;
	email: string;
	phone: string;
};

export function createUnitLeaseForm(context: UnitLeaseCreateContext): UnitLeaseCreateForm {
	return {
		leaseNumber: '',
		propertyId: String(context.unit.propertyId),
		unitId: String(context.unit.id),
		tenantId: '',
		tenantIds: [],
		startDate: '',
		endDate: '',
		monthlyRent: context.unit.marketRent > 0 ? String(context.unit.marketRent) : '',
		securityDeposit: '',
		lateFeeAmount: '75',
		rentDueDay: '1',
		rentTrackingStartMode: 'ForwardOnly',
		rentTrackingStartDate: '',
		status: 'Draft',
		notes: '',
	};
}

export function createSimpleTenantForm(): SimpleTenantForm {
	return {
		firstName: '',
		lastName: '',
		email: '',
		phone: '',
	};
}

export function validateSimpleTenantForm(form: SimpleTenantForm): Record<string, string> {
	const errors: Record<string, string> = {};
	if (!form.firstName.trim()) errors.firstName = 'First name is required';
	if (!form.lastName.trim()) errors.lastName = 'Last name is required';
	if (!form.email.trim()) {
		errors.email = 'Email is required';
	} else if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.email.trim())) {
		errors.email = 'Email must be a valid email address';
	}
	if (!form.phone.trim()) errors.phone = 'Phone is required';
	return errors;
}

export function buildSimpleTenantPayload(portfolioId: number, form: SimpleTenantForm) {
	return {
		portfolioId,
		firstName: form.firstName.trim(),
		lastName: form.lastName.trim(),
		email: form.email.trim(),
		phone: form.phone.trim(),
	};
}
