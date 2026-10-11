import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	accountingChangeForEntry,
	accountingAmountClass,
	formatAccountingCurrency,
	formatAccountingDate,
	formatAccountingDateTime,
	formatAccountTypeLabel,
	formatChangeLabel,
	formatJournalLineSide,
	formatJournalPostingLabel,
	formatSimpleJournalLineLabel,
	formatSimpleAccountGroupLabel,
	formatSourceRecordLabel,
	formatSourceTypeLabel,
	formatStatementStatusLabel,
	formatStatementTotalLabel,
	getIncreaseDecreaseAmounts,
	getJournalLineEntry
} from './accounting-display.ts';

describe('accounting display currency and dates', () => {
	it('formats positive, negative, zero, and absent amounts without financial math', () => {
		assert.equal(formatAccountingCurrency(1400), '$1,400.00');
		assert.equal(formatAccountingCurrency(-225), '-$225.00');
		assert.equal(formatAccountingCurrency(0), '$0.00');
		assert.equal(formatAccountingCurrency(null), '—');
		assert.equal(formatAccountingCurrency(undefined), '—');
		assert.equal(formatAccountingCurrency(Number.NaN), '—');
		assert.equal(formatAccountingCurrency(1050, 'CAD'), 'CA$1,050.00');
	});

	it('formats accounting date-only values in UTC and marks invalid facts clearly', () => {
		assert.equal(formatAccountingDate('2027-02-14T00:00:00Z'), 'Feb 14, 2027');
		assert.equal(formatAccountingDate('2027-02-14'), 'Feb 14, 2027');
		assert.equal(formatAccountingDate(null), '—');
		assert.equal(formatAccountingDate('not-a-date'), '—');
	});

	it('formats posted timestamps and keeps missing timestamps visible as absent', () => {
		const localTimestamp = new Date(2027, 1, 14, 15, 12);
		assert.equal(formatAccountingDateTime(localTimestamp), 'Feb 14, 2027, 3:12 PM');
		assert.equal(formatAccountingDateTime(undefined), '—');
	});
});

describe('accounting terminology display', () => {
	it('uses plain-language source labels for every accounting source type', () => {
		const expected: Record<string, string> = {
			TenantCharge: 'Tenant charge',
			TenantReceipt: 'Payment received',
			ProviderSettlement: 'Provider settlement',
			TenantConcession: 'Tenant credit',
			ReceivableWriteOff: 'Rent owed written off',
			SecurityDepositReceipt: 'Security deposit received',
			SecurityDepositRefund: 'Security deposit refund',
			SecurityDepositApplication: 'Security deposit applied',
			ExpensePayment: 'Expense paid',
			BillIncurred: 'Bill recorded',
			BillPayment: 'Bill paid',
			BankTransfer: 'Bank transfer',
			LoanPayment: 'Loan payment',
			CapitalPurchase: 'Capital purchase',
			Depreciation: 'Depreciation',
			OwnerContribution: 'Owner contribution',
			OwnerDistribution: 'Owner distribution',
			OpeningBalance: 'Opening balance'
		};

		for (const [sourceType, label] of Object.entries(expected)) {
			assert.equal(formatSourceTypeLabel(sourceType), label, sourceType);
		}
		assert.equal(formatSourceTypeLabel('ManualAdjustment'), 'Manual adjustment');
		assert.equal(formatSourceTypeLabel(null), '—');
	});

	it('keeps advanced account type labels and simple account groups distinct', () => {
		assert.equal(formatAccountTypeLabel('Asset'), 'Asset');
		assert.equal(formatAccountTypeLabel('Liability'), 'Liability');
		assert.equal(formatAccountTypeLabel('Expense'), 'Expense');
		assert.equal(formatSimpleAccountGroupLabel({ accountType: 'Income', name: 'Rental Income' }), 'Income');
		assert.equal(formatSimpleAccountGroupLabel({ accountType: 'Expense', name: 'Repairs and Maintenance' }), 'Expenses');
		assert.equal(formatSimpleAccountGroupLabel({ accountType: 'Asset', name: 'Operating Cash', code: '1000' }), 'Cash & bank');
		assert.equal(formatSimpleAccountGroupLabel({ accountType: 'Asset', name: 'Tenant Accounts Receivable', code: '1100' }), 'Money owed to you');
		assert.equal(formatSimpleAccountGroupLabel({ accountType: 'Liability', name: 'Security Deposits Payable' }), 'Money you owe');
		assert.equal(formatSimpleAccountGroupLabel({ accountType: 'Equity', name: 'Owner Contributions' }), 'Equity');
	});

	it('formats shared record and statement labels', () => {
		assert.equal(formatSourceRecordLabel('TenantReceipt', 482), 'Payment received #482');
		assert.equal(formatSourceRecordLabel(null, null), '—');
		assert.equal(formatStatementTotalLabel('Operating expenses'), 'Total operating expenses');
		assert.equal(formatStatementStatusLabel(true), 'Balanced');
		assert.equal(formatStatementStatusLabel(false), 'Not balanced');
		assert.equal(formatJournalPostingLabel(false), 'Posted');
		assert.equal(formatJournalPostingLabel(true), 'Correction');
	});
});

describe('normal-balance change labeling', () => {
	it('maps debit-normal and credit-normal entries without deriving amounts', () => {
		assert.equal(accountingChangeForEntry('Debit', 'debit'), 'increase');
		assert.equal(accountingChangeForEntry('Debit', 'credit'), 'decrease');
		assert.equal(accountingChangeForEntry('Credit', 'debit'), 'decrease');
		assert.equal(accountingChangeForEntry('Credit', 'credit'), 'increase');

		assert.deepEqual(
			getIncreaseDecreaseAmounts({ normalBalance: 'Debit', debitAmount: 1400, creditAmount: 0 }),
			{ increase: 1400, decrease: 0 }
		);
		assert.deepEqual(
			getIncreaseDecreaseAmounts({ normalBalance: 'Credit', debitAmount: 0, creditAmount: 1400 }),
			{ increase: 1400, decrease: 0 }
		);
		assert.equal(formatChangeLabel('increase'), 'increased');
		assert.equal(formatChangeLabel('decrease'), 'decreased');
		assert.equal(formatJournalLineSide('debit'), 'Debit');
		assert.equal(formatJournalLineSide('credit'), 'Credit');
		assert.deepEqual(
			getJournalLineEntry({ accountName: 'Operating Cash', debitAmount: 1400, creditAmount: 0 }),
			{ side: 'debit', amount: 1400 }
		);
		assert.equal(
			formatSimpleJournalLineLabel({
				accountName: 'Operating Cash',
				accountType: 'Asset',
				systemKey: 'tenant-accounts-receivable',
				debitAmount: 1400,
				creditAmount: 0,
				normalBalance: 'Debit',
				currency: 'USD'
			}),
			'Tenant now owes $1,400 more'
		);
		assert.equal(
			formatSimpleJournalLineLabel({ accountName: 'Tenant Accounts Receivable', accountType: 'Asset', systemKey: 'tenant-accounts-receivable', debitAmount: 0, creditAmount: 25, currency: 'USD' }),
			'Tenant now owes $25 less'
		);
		assert.equal(
			formatSimpleJournalLineLabel({ accountName: 'Rental Income', accountType: 'Income', systemKey: 'rental-income', debitAmount: 0, creditAmount: 1950, currency: 'USD', effectiveOn: '2026-08-01' }),
			'Counted as rent earned for August 2026'
		);
		assert.equal(accountingAmountClass(-1), 'text-destructive');
		assert.equal(accountingAmountClass(null), 'text-foreground');
	});
});
