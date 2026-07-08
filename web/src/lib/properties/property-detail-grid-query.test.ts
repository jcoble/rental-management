import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');

describe('property detail grid server-side query contracts', () => {
	it('drives the loans grid from the paged API with date filtering', () => {
		const component = source('../components/property/PropertyLoansSection.svelte');

		assert.match(component, /import RangeDatePicker from '\$lib\/components\/shared\/RangeDatePicker\.svelte';/);
		assert.match(component, /queryFn: \(\) => loans\.listPage\(\{[\s\S]*skip: \(loanPage - 1\) \* PAGE_SIZE[\s\S]*take: PAGE_SIZE[\s\S]*sort: loanSort \|\| undefined[\s\S]*from: loanFrom \|\| undefined[\s\S]*to: loanTo \|\| undefined/);
		assert.match(component, /serverSide/);
		assert.match(component, /totalCount=\{loansTotalCount\}/);
		assert.match(component, /onSortChange=\{\(sort\) => \{ loanSort = sort \?\? ''; loanPage = 1; \}\}/);
		assert.match(component, /testid="property-loans-date-range"/);
	});

	it('drives the recurring expenses grid from the paged API with date filtering', () => {
		const component = source('../components/property/PropertyRecurringExpensesSection.svelte');

		assert.match(component, /import RangeDatePicker from '\$lib\/components\/shared\/RangeDatePicker\.svelte';/);
		assert.match(component, /queryFn: \(\) => recurringExpenses\.listPage\(\{[\s\S]*skip: \(recurringPage - 1\) \* PAGE_SIZE[\s\S]*take: PAGE_SIZE[\s\S]*sort: recurringSort \|\| undefined[\s\S]*from: recurringFrom \|\| undefined[\s\S]*to: recurringTo \|\| undefined/);
		assert.match(component, /serverSide/);
		assert.match(component, /totalCount=\{totalCount\}/);
		assert.match(component, /onSortChange=\{\(sort\) => \{ recurringSort = sort \?\? ''; recurringPage = 1; \}\}/);
		assert.match(component, /testid="property-recurring-expenses-date-range"/);
	});
});
