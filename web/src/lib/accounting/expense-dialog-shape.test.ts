import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	EXPENSE_ESSENTIAL_FIELDS,
	EXPENSE_MORE_DETAIL_FIELDS,
	expenseDialogFields,
	hasMoreDetailValues,
	readLastExpenseProperty,
	rememberLastExpenseProperty,
	LAST_EXPENSE_PROPERTY_KEY
} from './expense-form.ts';

// Every field the old seven-step expense wizard collected (Source, Details, Dates, Context,
// Vendor, Receipt, Adjustments). The one-screen dialog must still collect all of them.
const FORMER_WIZARD_FIELDS = [
	'workOrderId',
	'propertyId',
	'unitId',
	'vendorId',
	'description',
	'amount',
	'subtotal',
	'taxAmount',
	'incurredAt',
	'dueDate',
	'paidAt',
	'category',
	'status',
	'billableToOwner',
	'notes',
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
];

describe('expense dialog shape', () => {
	it('asks for exactly five things on the first screen', () => {
		assert.deepEqual(
			[...EXPENSE_ESSENTIAL_FIELDS],
			['description', 'amount', 'incurredAt', 'propertyId', 'category']
		);
	});

	it('keeps every field the old wizard collected', () => {
		const shown = new Set<string>([...EXPENSE_ESSENTIAL_FIELDS, ...EXPENSE_MORE_DETAIL_FIELDS]);
		const missing = FORMER_WIZARD_FIELDS.filter((field) => !shown.has(field));
		assert.deepEqual(missing, [], `fields dropped from the expense dialog: ${missing.join(', ')}`);
		assert.equal(shown.size, FORMER_WIZARD_FIELDS.length);
	});

	it('never lists the same field in both places', () => {
		const essentials = new Set<string>(EXPENSE_ESSENTIAL_FIELDS);
		const overlap = EXPENSE_MORE_DETAIL_FIELDS.filter((field) => essentials.has(field));
		assert.deepEqual(overlap, []);
	});

	it('returns both lists together', () => {
		const fields = expenseDialogFields();
		assert.deepEqual(fields.essentials, [...EXPENSE_ESSENTIAL_FIELDS]);
		assert.deepEqual(fields.moreDetails, [...EXPENSE_MORE_DETAIL_FIELDS]);
	});

	it('opens More details when an expense already has extra values', () => {
		assert.equal(hasMoreDetailValues({ description: 'Roof patch', amount: '250' }), false);
		assert.equal(hasMoreDetailValues({ description: 'Roof patch', notes: '' }), false);
		assert.equal(hasMoreDetailValues({ billableToOwner: false }), false);
		assert.equal(hasMoreDetailValues({ notes: 'Split with the co-owner' }), true);
		assert.equal(hasMoreDetailValues({ billableToOwner: true }), true);
		assert.equal(hasMoreDetailValues({ vendorId: '7' }), true);
	});

	it('remembers the last rental used, and forgets it cleanly when storage is empty', () => {
		const store = new Map<string, string>();
		const storage = {
			getItem: (key: string) => store.get(key) ?? null,
			setItem: (key: string, value: string) => void store.set(key, value)
		};

		assert.deepEqual(readLastExpenseProperty(storage), { id: '', label: null });

		rememberLastExpenseProperty(storage, '12', 'Maple Street Duplex');
		assert.equal(store.get(LAST_EXPENSE_PROPERTY_KEY), '{"id":"12","label":"Maple Street Duplex"}');
		assert.deepEqual(readLastExpenseProperty(storage), { id: '12', label: 'Maple Street Duplex' });

		rememberLastExpenseProperty(storage, '', null);
		assert.deepEqual(readLastExpenseProperty(storage), { id: '', label: null });

		store.set(LAST_EXPENSE_PROPERTY_KEY, 'not json');
		assert.deepEqual(readLastExpenseProperty(storage), { id: '', label: null });
	});
});
