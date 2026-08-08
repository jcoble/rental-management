import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import { tenantLedgerPeriodRange } from './money.ts';
import { projectedRemainingCharge } from '../../accounting/tenant-credit-preview.ts';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');
const rentTab = source('./tabs/RentTab.svelte');
const panel = source('../accounting/TenantLedgerPanel.svelte');
const month = source('../accounting/TenantLedgerMonth.svelte');
const paymentSheet = source('../accounting/RecordPaymentSheet.svelte');
const chargeSheet = source('../accounting/OneTimeChargeSheet.svelte');
const creditSheet = source('../accounting/TenantCreditSheet.svelte');
const recurringSheet = source('../accounting/RecurringChargeSheet.svelte');

describe('W2 unit tenant ledger composition', () => {
	it('uses the canonical ledger surface and retires duplicate RentTab lists', () => {
		assert.match(rentTab, /AccountingDetailMode/);
		assert.match(rentTab, /TenantLedgerPanel/);
		assert.match(rentTab, /PaymentDetail/);
		assert.doesNotMatch(rentTab, /tenantAccounts\.(accountEntriesPage|chargesPage|depositsPage)/);
		assert.doesNotMatch(rentTab, /payments\.(recordReceipt|postCharge)/);
	});

	it('reads the three canonical server projections with the 12-month default and open-charge selector', () => {
		assert.match(panel, /tenantLedgers\.list/);
		assert.match(panel, /tenantLedgers\.monthSummary/);
		assert.match(panel, /tenantLedgers\.ledgerSummary/);
		assert.match(panel, /periodMonths = \$state[^\n]*12/);
		assert.match(panel, /const PERIODS[^\n]*3, 6, 9, 12/);
		assert.match(paymentSheet, /openOnly: true/);
		assert.match(panel, /Open charges/);
		assert.match(panel, /Payments/);
		assert.match(panel, /Credits & corrections/);
	});

	it('renders server balances and month totals without client financial math', () => {
		assert.match(month, /summary\.openingBalance/);
		assert.match(month, /summary\.chargeAmount/);
		assert.match(month, /summary\.paymentAmount/);
		assert.match(month, /summary\.closingBalance/);
		assert.match(month, /row\.runningAmountOwed/);
		assert.doesNotMatch(month, /chargeAmount\s*[-+]/);
		assert.doesNotMatch(month, /paymentAmount\s*[-+]/);
	});

	it('exposes all W2 actions and the three fix-charge commands', () => {
		for (const label of ['Record payment', 'Add charge', 'Give credit', 'Recurring charge', 'Scan payment']) {
			assert.match(panel, new RegExp(label.replace(/[&]/g, '\\&')));
		}
		assert.match(panel, /Posts a credit applied to this charge/);
		assert.match(panel, /Posts an additional charge/);
		assert.match(panel, /Reverses the charge/);
		assert.match(panel, /tenantMoney\.reverseCharge/);
	});

	it('keeps sheet fields and server-side correction boundaries explicit', () => {
		for (const label of ['Amount', 'Date received', 'Payment method', 'Reference / check #', 'Payer', 'Note', 'Receipt document']) {
			assert.match(paymentSheet, new RegExp(label.replace(/[/#]/g, '\\$&')));
		}
		for (const label of ['What is this charge for?', 'Effective date', 'Due date', 'Service period', 'Description', 'Document']) {
			assert.match(chargeSheet, new RegExp(label));
		}
		for (const label of ['Amount', 'Effective date', 'Reason', 'Original charge', 'Preview', 'Document']) {
			assert.match(creditSheet, new RegExp(label));
		}
		for (const label of ['Name', 'Amount', 'Category', 'Starts', 'Ends', 'Day due']) {
			assert.match(recurringSheet, new RegExp(label));
		}
		assert.match(creditSheet, /DepositCharge/);
		assert.match(creditSheet, /selectedTarget\.openAmount/);
		assert.match(creditSheet, /formatAccountingCurrency\(projectedRemainingAmount, currency\)/);
		assert.match(
			creditSheet,
			/This credit is larger than what's left of the original charge\. Enter it as a standalone credit instead\./
		);
	});

	it('projects the remaining targeted charge live and safely', () => {
		assert.equal(projectedRemainingCharge(500, '125.50'), 374.5);
		assert.equal(projectedRemainingCharge(500, '700'), 0);
		assert.equal(projectedRemainingCharge(500, ''), 500);
		assert.equal(projectedRemainingCharge(500, 'not-a-number'), 500);
	});

	it('computes only the date window for the server query', () => {
		const anchor = new Date('2027-02-15T00:00:00Z');
		assert.deepEqual(tenantLedgerPeriodRange(12, anchor), {
			from: '2026-03-01',
			to: '2027-02-28'
		});
		assert.deepEqual(tenantLedgerPeriodRange(3, anchor), {
			from: '2026-12-01',
			to: '2027-02-28'
		});
	});

	it('keeps the specified empty state and generic unavailable state', () => {
		assert.match(panel, /No charges or payments yet\. Record the first payment or charge above\./);
		assert.match(panel, /This money view is not available\./);
		assert.match(panel, /LoadingState/);
	});

	it('keeps ledger rows available when optional summary reads fail', () => {
		assert.match(panel, /const coreReadError = \$derived\(ledgerQuery\.error \?\? monthSummaryQuery\.error\)/);
		assert.match(panel, /const summaryReadError = \$derived\(accountQuery\.error \?\? ledgerSummaryQuery\.error\)/);
		assert.match(panel, /data-testid="tenant-ledger-summary-error"/);
		assert.doesNotMatch(panel, /ledgerSummaryQuery\.isLoading\s*\)/);
	});
});
