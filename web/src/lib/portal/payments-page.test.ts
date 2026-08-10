import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const pageSource = readFileSync(
	new URL('../../routes/(portal)/portal/payments/+page.svelte', import.meta.url),
	'utf8'
);
const dashboardSource = readFileSync(
	new URL('../../routes/(portal)/portal/+page.svelte', import.meta.url),
	'utf8'
);
const endpointSource = readFileSync(new URL('../api/endpoints/portal.ts', import.meta.url), 'utf8');
const historyRowsSource = readFileSync(
	new URL('../components/accounting/PortalAccountHistoryRows.svelte', import.meta.url),
	'utf8'
);

describe('canonical portal money contract', () => {
	it('uses only tenant-account page, detail, entries, charges, and deposit reads', () => {
		assert.match(endpointSource, /\/portal\/tenant-accounts\/page/);
		assert.match(endpointSource, /\/portal\/tenant-accounts\/\$\{tenantAccountId\}/);
		assert.match(endpointSource, /\/entries\/page/);
		assert.match(endpointSource, /\/charges\/page/);
		assert.match(endpointSource, /\/deposit/);
		assert.doesNotMatch(endpointSource, /api\.get[^\n]*\/portal\/balance/);
		assert.doesNotMatch(endpointSource, /api\.get[^\n]*\/portal\/payments/);
	});

	it('requires an explicit selection when more than one account is available', () => {
		assert.match(pageSource, /accountsQuery\.data\.totalCount > 1/);
		assert.match(pageSource, /Choose an account/);
		assert.match(pageSource, /selectedAccountId == null/);
		assert.match(dashboardSource, /accountsQuery\.data\.totalCount > 1/);
		assert.match(dashboardSource, /portal-dashboard-account-selector/);
	});

	it('requests server-owned account-history paging and uses exact ledger-entry checkout identity', () => {
		assert.match(pageSource, /tenantAccountHistory\(selectedAccountId as number/);
		assert.match(pageSource, /skip,/);
		assert.match(pageSource, /take: pageSize/);
		assert.match(pageSource, /entry\.tenantLedgerEntryId/);
		assert.doesNotMatch(pageSource, /\.filter\(/);
		assert.doesNotMatch(pageSource, /\.sort\(/);
	});

	it('preserves notification account and ledger-entry focus without client-side filtering', () => {
		assert.match(pageSource, /page\.url\.searchParams\.get\('account'\)/);
		assert.match(pageSource, /page\.url\.searchParams\.get\('entry'\)/);
		assert.match(pageSource, /entry: focusedEntryId/);
		assert.match(historyRowsSource, /id=\{`portal-ledger-entry-\$\{entry\.tenantLedgerEntryId\}`\}/);
		assert.match(historyRowsSource, /data-focused=\{entry\.isFocused\}/);
		assert.match(pageSource, /url\.searchParams\.delete\('entry'\)/);
		assert.doesNotMatch(pageSource, /historyQuery\.data\?\.items\.filter/);
		assert.doesNotMatch(pageSource, /historyQuery\.data\?\.items\.find/);
	});

	it('keeps online-payment availability gentle and account-scoped', () => {
		assert.match(endpointSource, /onlinePaymentsAvailable: boolean/);
		assert.match(pageSource, /autopayQuery\.data\?\.onlinePaymentsAvailable === false/);
		assert.match(pageSource, /Autopay is not available right now\./);
		assert.match(pageSource, /queryKey: \['portal-autopay', selectedAccountId\]/);
	});

	it('uses stable loaders and retry actions for payment reads', () => {
		assert.match(pageSource, /LoadingState/);
		assert.match(pageSource, /portal-payment-accounts-loading/);
		assert.match(pageSource, /portal-autopay-loading/);
		assert.match(pageSource, /portal-history-loading/);
		assert.match(pageSource, /accountsQuery\.refetch\(\)/);
		assert.match(pageSource, /autopayQuery\.refetch\(\)/);
		assert.match(pageSource, /historyQuery\.refetch\(\)/);
	});
});
