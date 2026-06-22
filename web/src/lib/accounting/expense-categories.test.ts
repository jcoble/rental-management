import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { EXPENSE_CATEGORY_OPTIONS, formatExpenseCategory } from './expense-categories.ts';

describe('expense category labels', () => {
	it('formats schedule E category enums as landlord-readable labels', () => {
		assert.equal(formatExpenseCategory('AutoTravel'), 'Auto & travel');
		assert.equal(formatExpenseCategory('CleaningMaintenance'), 'Cleaning & maintenance');
		assert.equal(formatExpenseCategory('LegalProfessional'), 'Legal & professional fees');
		assert.equal(formatExpenseCategory('ManagementFees'), 'Management fees');
		assert.equal(formatExpenseCategory('MortgageInterest'), 'Mortgage interest');
		assert.equal(formatExpenseCategory('Repairs'), 'Repairs & maintenance');
	});

	it('exposes category select options with stable API values and readable labels', () => {
		assert.deepEqual(
			EXPENSE_CATEGORY_OPTIONS.filter((option) =>
				['AutoTravel', 'CleaningMaintenance', 'LegalProfessional', 'Repairs'].includes(option.value)
			),
			[
				{ value: 'AutoTravel', label: 'Auto & travel' },
				{ value: 'CleaningMaintenance', label: 'Cleaning & maintenance' },
				{ value: 'LegalProfessional', label: 'Legal & professional fees' },
				{ value: 'Repairs', label: 'Repairs & maintenance' }
			]
		);
	});
});
