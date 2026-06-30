const usdFormatter = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' });

export type PaymentLeaseLabelSource = {
	propertyName?: string | null;
	unitNumber?: string | null;
	tenantName?: string | null;
	leaseNumber?: string | null;
};

export function formatPaymentMoney(value: number | string | null | undefined): string {
	const amount = Number(value);
	return usdFormatter.format(Number.isFinite(amount) ? amount : 0);
}

function clean(value: string | null | undefined): string {
	return value?.trim() ?? '';
}

function homeLabel(source: PaymentLeaseLabelSource): string {
	const property = clean(source.propertyName);
	const unit = clean(source.unitNumber);
	if (property && unit) return `${property} · Unit ${unit}`;
	if (property) return property;
	if (unit) return `Unit ${unit}`;
	return '';
}

export function formatPaymentLeaseDisplay(source: PaymentLeaseLabelSource | null | undefined): string {
	if (!source) return '';
	return homeLabel(source) || clean(source.tenantName) || clean(source.leaseNumber);
}

export function formatLeasePickerLabel(source: PaymentLeaseLabelSource): string {
	const home = homeLabel(source);
	const tenant = clean(source.tenantName);
	const leaseNumber = clean(source.leaseNumber);
	const lead = home || tenant || leaseNumber || 'Lease';
	const secondary = [home ? tenant : '', lead !== leaseNumber ? leaseNumber : ''].filter(Boolean);

	return secondary.length ? `${lead} — ${secondary.join(' · ')}` : lead;
}
