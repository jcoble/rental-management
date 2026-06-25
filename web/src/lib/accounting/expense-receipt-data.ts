export interface ExpenseReceiptLineItemInput {
	description: string;
	quantity: string;
	unitPrice: string;
	amount: string;
}

export interface ExpenseReceiptDataSaveInput {
	rawReceiptData: string;
	lineItems: ExpenseReceiptLineItemInput[];
}

export interface ExpenseReceiptDataSavePayload {
	receiptData: string | null;
	clearReceiptData: boolean;
}

function parseAmount(raw: string | undefined | null): number | null {
	if (raw == null) return null;
	const cleaned = String(raw).replace(/[^0-9.\-]/g, '');
	if (cleaned === '' || cleaned === '-' || cleaned === '.') return null;
	const n = Number(cleaned);
	return Number.isFinite(n) ? n : null;
}

function hasLineItemValue(item: ExpenseReceiptLineItemInput): boolean {
	return (
		item.description.trim() !== '' ||
		parseAmount(item.quantity) != null ||
		parseAmount(item.unitPrice) != null ||
		parseAmount(item.amount) != null
	);
}

function buildLineItem(item: ExpenseReceiptLineItemInput) {
	const row: Record<string, string | number> = {};
	const description = item.description.trim();
	const quantity = parseAmount(item.quantity);
	const unitPrice = parseAmount(item.unitPrice);
	const amount = parseAmount(item.amount);

	if (description !== '') row.description = description;
	if (quantity != null) row.quantity = quantity;
	if (unitPrice != null) row.unitPrice = unitPrice;
	if (amount != null) row.amount = amount;

	return row;
}

export function buildExpenseReceiptDataForSave({
	rawReceiptData,
	lineItems,
}: ExpenseReceiptDataSaveInput): ExpenseReceiptDataSavePayload {
	const trimmed = rawReceiptData.trim();
	if (trimmed === '') {
		return { receiptData: null, clearReceiptData: true };
	}

	try {
		const parsed = JSON.parse(trimmed);
		if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
			return { receiptData: trimmed, clearReceiptData: false };
		}

		return {
			receiptData: JSON.stringify({
				...parsed,
				lineItems: lineItems.filter(hasLineItemValue).map(buildLineItem),
			}),
			clearReceiptData: false,
		};
	} catch {
		return { receiptData: trimmed, clearReceiptData: false };
	}
}
