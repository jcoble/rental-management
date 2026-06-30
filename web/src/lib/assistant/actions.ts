const ACTION_VERB = /\b(log|record|add|create|save|enter|book)\b/i;
const EXPENSE_NOUN = /\b(expense|receipt|bill|invoice|paid)\b/i;

export function looksLikeAssistantActionCommand(input: string): boolean {
	const text = input.trim();
	return ACTION_VERB.test(text) && EXPENSE_NOUN.test(text);
}

export function formatAssistantMoney(amount?: number | null): string {
	if (amount == null || Number.isNaN(amount)) return 'unknown amount';
	return new Intl.NumberFormat('en-US', {
		style: 'currency',
		currency: 'USD'
	}).format(amount);
}

export function assistantActionFieldLabel(field: string): string {
	switch (field) {
		case 'amount':
			return 'amount';
		case 'description':
			return 'description';
		case 'expense details':
			return 'expense details';
		default:
			return field.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
	}
}
