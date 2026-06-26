import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');

describe('property loan notes UI contract', () => {
	it('threads loan notes through the add/edit loan form', () => {
		const component = source('../components/property/PropertyLoansSection.svelte');
		const schemaSource = source('../schemas/index.ts');
		const endpointSource = source('../api/endpoints/loans.ts');

		assert.match(schemaSource, /export const loanSchema = z\.object\(\{[\s\S]*notes: optionalText/);
		assert.match(endpointSource, /export interface Loan \{[\s\S]*notes\?: string \| null;/);

		assert.match(component, /const emptyLoan = \{[\s\S]*notes: ''/);
		assert.match(component, /notes: l\.notes \?\? ''/);
		assert.match(
			component,
			/<InlineField label="Notes"[\s\S]*bind:value=\{form\.notes\}[\s\S]*editing[\s\S]*type="textarea"[\s\S]*testid="loan-notes"/
		);
	});
});
