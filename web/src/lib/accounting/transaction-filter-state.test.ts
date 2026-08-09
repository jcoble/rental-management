import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
	normalizeTransactionFilters,
	normalizeTransactionKind,
	TRANSACTION_KIND_OPTIONS,
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
	assert.deepEqual(
		TRANSACTION_KIND_OPTIONS.map((option) => option.value),
		['Payment', 'Expense']
	);
	assert.deepEqual(transactionStatusesForKind('Bank'), []);
	assert.deepEqual(transactionCategoriesForKind('Bank'), []);
	assert.equal(normalizeTransactionKind('Bank'), '');
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

test('normalizes every unsupported kind/status pair before the request', () => {
	const allStatuses = ['Credit', 'Pending', 'Approved', 'Paid'];
	for (const kind of ['Payment', 'Expense']) {
		const offered = transactionStatusesForKind(kind);
		for (const status of allStatuses) {
			const normalized = normalizeTransactionFilters(kind, status, '');
			assert.equal(normalized.status, offered.includes(status) ? status : '', `${kind}/${status}`);
		}
	}

	assert.deepEqual(normalizeTransactionFilters('Bank', 'Matched', 'Deposit'), { status: '', category: '' });
});
