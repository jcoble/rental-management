import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { businessDateOrToday, localIsoDate } from '$lib/utils/business-date';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');

describe('money-entry date defaults', () => {
	it('normalizes the portfolio date and uses a local calendar fallback', () => {
		expect(businessDateOrToday('2027-02-28T00:00:00Z')).toBe('2027-02-28');
		const localDate = {
			getFullYear: () => 2027,
			getMonth: () => 2,
			getDate: () => 31
		} as unknown as Date;
		expect(localIsoDate(localDate)).toBe('2027-03-31');
		expect(businessDateOrToday('', localDate)).toBe('2027-03-31');
		expect(businessDateOrToday(null, localDate)).toBe('2027-03-31');
	});

	it('wires the supplied business date through every money-entry surface', () => {
		const sheets = [
			'../src/lib/components/accounting/OneTimeChargeSheet.svelte',
			'../src/lib/components/accounting/RecordPaymentSheet.svelte',
			'../src/lib/components/accounting/TenantCreditSheet.svelte',
			'../src/lib/components/accounting/RecurringChargeSheet.svelte',
			'../src/lib/components/accounting/PastDuePaymentDialog.svelte',
			'../src/lib/components/unit/tabs/LeaseTab.svelte',
			'../src/lib/components/accounting/TenantLedgerPanel.svelte',
			'../src/lib/components/records/PaymentDetail.svelte',
			'../src/lib/components/unit/money.ts',
			'../src/routes/(protected)/units/[id]/+page.svelte',
			'../src/routes/(protected)/deposits/[id]/+page.svelte'
		];

		for (const path of sheets) {
			const contents = source(path);
			expect(contents).toContain("businessDateOrToday");
			expect(contents).not.toContain('new Date().toISOString().slice(0, 10)');
		}
	});

	it('does not re-key an open form or move-in dialog when the date query changes', () => {
		const sourceByPath = (path: string) => source(path);
		expect(sourceByPath('../src/lib/components/accounting/OneTimeChargeSheet.svelte')).not.toContain('${moneyDate}|');
		expect(sourceByPath('../src/lib/components/accounting/RecordPaymentSheet.svelte')).not.toContain('${moneyDate}|');
		expect(sourceByPath('../src/lib/components/accounting/TenantCreditSheet.svelte')).not.toContain('${moneyDate}|');
		expect(sourceByPath('../src/lib/components/accounting/RecurringChargeSheet.svelte')).not.toContain('${moneyDate}|');
		const unitPage = sourceByPath('../src/routes/(protected)/units/[id]/+page.svelte');
		expect(unitPage).not.toContain(':${moveInBusinessDate}');
		expect(unitPage).toContain('!moveInContextQuery.data && moveInContextQuery.isPending');
	});
});
