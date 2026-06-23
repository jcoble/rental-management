const usdFormatter = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' });

export function formatPaymentMoney(value: number | string | null | undefined): string {
	const amount = Number(value);
	return usdFormatter.format(Number.isFinite(amount) ? amount : 0);
}
