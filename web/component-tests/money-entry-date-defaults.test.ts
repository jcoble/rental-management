import { readFileSync } from 'node:fs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { businessDateOrToday, localIsoDate } from '$lib/utils/business-date';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');
const originalTimeZone = process.env.TZ;

describe('money-entry date defaults', () => {
	afterEach(() => {
		vi.useRealTimers();
		if (originalTimeZone === undefined) delete process.env.TZ;
		else process.env.TZ = originalTimeZone;
	});

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

	it('defaults the unit expense and onboarding lease forms to the local calendar date', () => {
		process.env.TZ = 'America/Los_Angeles';
		vi.useFakeTimers();
		vi.setSystemTime(new Date('2027-03-01T00:30:00Z'));

		expect(businessDateOrToday(undefined)).toBe('2027-02-28');

		const expensesTab = source('../src/lib/components/unit/tabs/ExpensesTab.svelte');
		expect(expensesTab.includes('const moneyDate = $derived(businessDateOrToday(businessDate))')).toBe(true);
		expect(expensesTab.includes('incurredAt: moneyDate')).toBe(true);
		expect(expensesTab.includes('new Date().toISOString().slice(0, 10)')).toBe(false);

		const onboardingPage = source('../src/routes/(protected)/onboarding/+page.svelte');
		expect(onboardingPage.includes('startDate: businessDateOrToday(undefined, today)')).toBe(true);
		expect(onboardingPage.includes('endDate: businessDateOrToday(undefined, oneYear)')).toBe(true);
		expect(onboardingPage.includes('const isoDate = (d: Date) => d.toISOString().slice(0, 10)')).toBe(false);
	});
});
