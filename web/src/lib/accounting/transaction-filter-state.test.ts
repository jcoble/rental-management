import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
	normalizeTransactionFilters,
	transactionCategoriesForKind,
	transactionStatusesForKind
} from './transaction-filter-state.ts';

test('S18-BUG-1 clears a status that is not valid for the selected transaction kind', () => {
	assert.deepEqual(
		normalizeTransactionFilters('Payment', 'Approved', ''),
		{ status: '', category: '' }
	);
	assert.deepEqual(
		normalizeTransactionFilters('Expense', 'Approved', ''),
		{ status: 'Approved', category: '' }
	);
});

test('S18-BUG-2 clears a category that is not valid for the selected transaction kind', () => {
	assert.deepEqual(
		normalizeTransactionFilters('Payment', '', 'Repairs'),
		{ status: '', category: '' }
	);
	assert.deepEqual(
		normalizeTransactionFilters('Expense', '', 'Repairs'),
		{ status: '', category: 'Repairs' }
	);
});

test('transaction filter options retain only values supported by the active kind', () => {
	assert.ok(transactionStatusesForKind('Bank').includes('Matched'));
	assert.ok(!transactionStatusesForKind('Bank').includes('Paid'));
	assert.ok(transactionCategoriesForKind('Payment').some((option) => option.value === 'RentCharge'));
	assert.ok(!transactionCategoriesForKind('Payment').some((option) => option.value === 'Repairs'));
});

test('S18-BUG-1 exposes the status vocabulary projected for payment rows', () => {
	assert.deepEqual(transactionStatusesForKind('Payment'), ['Credit']);
	assert.deepEqual(
		normalizeTransactionFilters('Payment', 'Credit', ''),
		{ status: 'Credit', category: '' }
	);
	assert.deepEqual(
		normalizeTransactionFilters('Payment', 'Paid', ''),
		{ status: '', category: '' }
	);
});
