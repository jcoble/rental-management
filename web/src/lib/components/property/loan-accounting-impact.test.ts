import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const loansSection = readFileSync(new URL('./PropertyLoansSection.svelte', import.meta.url), 'utf8');

describe('loan payment accounting impact contract', () => {
	it('uses the paid loan payment ID below its schedule fields', () => {
		assert.match(
			loansSection,
			/import AccountingImpactCard from '\$lib\/components\/accounting\/AccountingImpactCard\.svelte';/
		);
		assert.match(
			loansSection,
			/<AccountingImpactCard sourceType="LoanPayment" sourceId=\{row\.id\} \/>/
		);
		const paidBlockIndex = loansSection.indexOf('{#if row.status === \'Paid\'}');
		const impactIndex = loansSection.indexOf('<AccountingImpactCard sourceType="LoanPayment"');
		assert.ok(impactIndex > paidBlockIndex);
		assert.ok(impactIndex < loansSection.indexOf('{#if row.paymentDoesNotCoverInterest}'));
	});
});
