import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	confirmExpenseDelete,
	expenseDeleteConfirmMessage,
	formatExpenseUnitReference,
	requestExpenseDelete
} from './expense-detail-actions.ts';

describe('expense detail delete actions', () => {
	it('clicking delete selects a confirmation target without deleting immediately', () => {
		const deletedIds: number[] = [];
		const target = requestExpenseDelete({ id: 42, description: 'Apex Plumbing' });

		assert.deepEqual(deletedIds, []);
		assert.deepEqual(target, { id: 42, description: 'Apex Plumbing' });
		assert.equal(expenseDeleteConfirmMessage(target), 'Delete "Apex Plumbing"? This cannot be undone.');
	});

	it('confirming delete invokes the supplied delete action for the selected target', () => {
		const deletedIds: number[] = [];
		const target = requestExpenseDelete({ id: 42, description: 'Apex Plumbing' });

		confirmExpenseDelete(target, (id) => deletedIds.push(id));

		assert.deepEqual(deletedIds, [42]);
	});

	it('does nothing when confirm runs without a selected target', () => {
		const deletedIds: number[] = [];

		confirmExpenseDelete(null, (id) => deletedIds.push(id));

		assert.deepEqual(deletedIds, []);
		assert.equal(expenseDeleteConfirmMessage(null), '');
	});
});

describe('expense detail reference labels', () => {
	it('shows the linked unit number instead of hiding unit-grounded expenses', () => {
		assert.equal(formatExpenseUnitReference({ unitId: 7, unitNumber: '1A' }), 'Unit 1A');
		assert.equal(formatExpenseUnitReference({ unitId: 7, unitNumber: 'Unit 2B' }), 'Unit 2B');
	});

	it('uses a clear empty label when an expense has no unit', () => {
		assert.equal(formatExpenseUnitReference({ unitId: null, unitNumber: null }), 'No unit');
	});
});
