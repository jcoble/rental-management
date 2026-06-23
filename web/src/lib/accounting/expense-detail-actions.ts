export type ExpenseDeleteTarget = {
	id: number;
	description: string;
};

export type ExpenseUnitReference = {
	unitId?: number | null;
	unitNumber?: string | null;
};

export function formatExpenseUnitReference(expense: ExpenseUnitReference): string {
	const unitNumber = expense.unitNumber?.trim();
	if (!expense.unitId) return 'No unit';
	if (!unitNumber) return `Unit #${expense.unitId}`;
	return unitNumber.toLowerCase().startsWith('unit ') ? unitNumber : `Unit ${unitNumber}`;
}

export function requestExpenseDelete(expense: ExpenseDeleteTarget): ExpenseDeleteTarget {
	return { id: expense.id, description: expense.description };
}

export function expenseDeleteConfirmMessage(target: ExpenseDeleteTarget | null): string {
	return target ? `Delete "${target.description}"? This cannot be undone.` : '';
}

export function confirmExpenseDelete(
	target: ExpenseDeleteTarget | null,
	deleteExpense: (id: number) => void
): void {
	if (!target) return;
	deleteExpense(target.id);
}
