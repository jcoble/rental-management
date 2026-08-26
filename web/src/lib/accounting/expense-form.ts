/**
 * Shape of the one-screen "Add expense" dialog.
 *
 * The five essentials are all a landlord has to answer to record a spend; everything else the
 * expense record can hold sits behind the "More details" disclosure in the same dialog. Together
 * the two lists cover every field the old seven-step wizard collected, so nothing was dropped.
 */
export const EXPENSE_ESSENTIAL_FIELDS = [
	'description',
	'amount',
	'incurredAt',
	'propertyId',
	'category'
] as const;

export const EXPENSE_MORE_DETAIL_FIELDS = [
	'unitId',
	'vendorId',
	'workOrderId',
	'status',
	'billableToOwner',
	'notes',
	'subtotal',
	'taxAmount',
	'dueDate',
	'paidAt',
	'vendorAddress',
	'vendorPhone',
	'vendorWebsite',
	'vendorTaxId',
	'receiptNumber',
	'paymentMethod',
	'cardLast4',
	'taxRate',
	'tip',
	'discount',
	'shipping'
] as const;

export function expenseDialogFields() {
	return {
		essentials: [...EXPENSE_ESSENTIAL_FIELDS],
		moreDetails: [...EXPENSE_MORE_DETAIL_FIELDS]
	};
}

/** True when an existing expense already carries something from "More details", so editing opens it. */
export function hasMoreDetailValues(form: Record<string, unknown>) {
	return EXPENSE_MORE_DETAIL_FIELDS.some((field) => {
		const value = form[field];
		if (typeof value === 'boolean') return value;
		return String(value ?? '').trim() !== '';
	});
}

/** Where the last rental an expense was filed against is remembered, so the next one starts there. */
export const LAST_EXPENSE_PROPERTY_KEY = 'rc.expense.lastPropertyId';

type PropertyMemoryStore = {
	getItem(key: string): string | null;
	setItem(key: string, value: string): void;
};

export function readLastExpenseProperty(store: PropertyMemoryStore | null | undefined) {
	const empty = { id: '', label: null as string | null };
	if (!store) return empty;
	try {
		const raw = store.getItem(LAST_EXPENSE_PROPERTY_KEY);
		if (!raw) return empty;
		const parsed = JSON.parse(raw) as { id?: unknown; label?: unknown };
		const id = typeof parsed?.id === 'string' ? parsed.id : '';
		if (!id) return empty;
		return { id, label: typeof parsed.label === 'string' ? parsed.label : null };
	} catch {
		return empty;
	}
}

export function rememberLastExpenseProperty(
	store: PropertyMemoryStore | null | undefined,
	id: string,
	label: string | null
) {
	if (!store) return;
	try {
		store.setItem(LAST_EXPENSE_PROPERTY_KEY, JSON.stringify({ id, label: label ?? null }));
	} catch {
		// A browser with storage turned off just does not remember the last rental.
	}
}
