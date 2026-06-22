export type ExpenseDeleteTarget = {
	id: number;
	description: string;
};

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
