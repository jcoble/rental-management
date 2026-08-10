import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const page = readFileSync(new URL('./+page.svelte', import.meta.url), 'utf8');
const api = readFileSync(new URL('../../../../lib/api/endpoints/portal.ts', import.meta.url), 'utf8');
const helper = readFileSync(new URL('../../../../lib/portal/tenant-ledger.ts', import.meta.url), 'utf8');

describe('tenant account history contract', () => {
	test('uses the authorized portal account summary and canonical history response', () => {
		assert.match(page, /portal\.tenantAccountsPage/);
		assert.match(page, /portal\.tenantAccountHistory/);
		assert.match(page, /portal\.leases\(\)/);
		assert.match(api, /\/portal\/tenant-accounts\/page/);
		assert.match(api, /\/history\$\{queryString\(params\)\}/);
		assert.doesNotMatch(page, /tenantAccounts\.|tenantLedgers\.|accountNumber/);
	});

	test('renders the four server-owned balance facts without client-side financial math', () => {
		for (const contract of [
			'portal-current-balance',
			'portal-past-due',
			'portal-next-due',
			'portal-deposit-held',
			'selectedAccount.receivableBalance',
			'selectedAccount.pastDueAmount',
			'selectedAccount.nextDueAmount',
			'selectedAccount.deposit.heldBalance',
			'formatAccountingCurrency(history.closingBalance'
		]) {
			assert.ok(page.includes(contract), `missing ${contract}`);
		}
		assert.doesNotMatch(page, /Math\.(abs|round|ceil|floor)\(/);
		assert.doesNotMatch(page, /totalDebits|totalCredits|journalId|journalEntry/);
	});

	test('keeps account-history rows in tenant vocabulary and binds each row balance to the API', () => {
		for (const label of [
			'Rent charge',
			'Late fee',
			'Payment received — thank you',
			'Credit',
			'Refund',
			'Correction'
		]) {
			assert.match(helper, new RegExp(label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
		}
		assert.match(page, /tenantLedgerLabel\(entry\)/);
		assert.match(page, /entry\.signedAmount/);
		assert.match(page, /entry\.runningBalance/);
		assert.doesNotMatch(page, /\{entry\.description\}/);
		assert.doesNotMatch(page, /\{entry\.displayType\}/);
		assert.doesNotMatch(page, /entry\.entryType\.replace/);
		assert.doesNotMatch(page, /\.sort\(|\.filter\(/);
	});

	test('renders server-provided payment allocations for tenant-facing rows', () => {
		assert.match(page, /TenantPaymentAllocationDetails/);
		assert.match(page, /entry\.allocations/);
		assert.match(page, /portal-payment-allocations-/);
		assert.match(api, /interface PortalTenantAllocationRef/);
	});

	test('groups account history by month with server-owned month-end balances and contextual help', () => {
		assert.match(page, /portal-history-month-/);
		assert.match(page, /monthLabel\(entry\.effectiveOn\)/);
		assert.match(page, /Month-end balance/);
		assert.match(page, /entry\.runningBalance/);
		assert.match(page, /ACCOUNTING_HELP\.tenantLedger/);
		assert.match(page, /portal-account-history-help/);
	});

	test('provides loading, empty, error, retry, pagination, and focused-entry states', () => {
		for (const contract of [
			'portal-payment-accounts-loading',
			'portal-account-empty',
			'portal-account-error',
			'portal-history-loading',
			'portal-history-error',
			'accountsQuery.refetch()',
			'historyQuery.refetch()',
			'data-focused={entry.isFocused}',
			'scrollIntoView',
			'portal-beginning-balance',
			'portal-closing-balance'
		]) {
			assert.ok(page.includes(contract), `missing ${contract}`);
		}
	});

	test('prints a tenant-safe HTML statement with server-provided date range and closing balance', () => {
		for (const contract of [
			'portal-print-statement',
			'window.print()',
			'print-statement',
			'@media print',
			'selectedAccount.propertyName',
			'selectedAccount.unitNumber',
			'selectedLease?.tenantName',
			'history.periodFrom',
			'history.periodTo',
			'history.closingBalance'
		]) {
			assert.ok(page.includes(contract), `missing ${contract}`);
		}
		assert.doesNotMatch(page, /account codes|debits|credits|journal IDs|other tenants|other units/i);
	});
});
