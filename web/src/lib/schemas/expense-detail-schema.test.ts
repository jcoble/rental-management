/**
 * Regression test for the expense DETAIL page Save.
 *
 * The detail page's `form` only carries the fields it actually edits, but it was
 * validating against the full `expenseSchema` (which also has workOrderId + the
 * scan/receipt fields). Zod's `.nullable()` rejects `undefined` (a missing key),
 * so every absent field errored, `saveExpense` early-returned, and Save silently
 * did nothing. `expenseDetailSchema` validates only the detail form's fields.
 *
 *   node --test --experimental-strip-types src/lib/schemas/expense-detail-schema.test.ts
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { expenseDetailSchema, parseForm } from './index.ts';

// Exactly the keys the expense detail page's `form` $state holds.
const detailForm = {
	description: 'Drain repair',
	amount: '42.50',
	incurredAt: '2026-01-01',
	category: 'Repairs',
	status: 'Pending',
	propertyId: '',
	vendorId: '',
	dueDate: '',
	paidAt: '',
	billableToOwner: false,
	notes: '',
	subtotal: '',
	taxAmount: ''
};

test('expenseDetailSchema accepts the detail form (no spurious missing-field errors)', () => {
	const result = parseForm(expenseDetailSchema, detailForm);
	assert.equal(result.errors, null, `unexpected errors: ${JSON.stringify(result.errors)}`);
	assert.equal(result.data?.description, 'Drain repair');
	assert.equal(result.data?.amount, 42.5);
	assert.equal(result.data?.propertyId, null); // '' -> null
});

test('expenseDetailSchema still enforces required fields (empty description/amount)', () => {
	const result = parseForm(expenseDetailSchema, { ...detailForm, description: '', amount: '' });
	assert.ok(result.errors, 'expected validation errors for empty required fields');
	assert.ok(result.errors?.description, 'expected a description error');
	assert.ok(result.errors?.amount, 'expected an amount error');
});
