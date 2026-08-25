export type ExpenseReceiptTotalSource = {
	amount?: number | null;
	subtotal?: number | null;
	taxAmount?: number | null;
};

export function receiptGrandTotal(source: ExpenseReceiptTotalSource): number {
	if (typeof source.amount === 'number' && Number.isFinite(source.amount)) {
		return source.amount;
	}

	const subtotal =
		typeof source.subtotal === 'number' && Number.isFinite(source.subtotal) ? source.subtotal : 0;
	const tax =
		typeof source.taxAmount === 'number' && Number.isFinite(source.taxAmount) ? source.taxAmount : 0;
	return subtotal + tax;
}
