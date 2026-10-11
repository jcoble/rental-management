import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	GENERAL_LEDGER_DEFAULT_SORT,
	buildMoneyUrlQuery,
	generalLedgerUrlValues,
	normalizeMoneyTab,
	readGeneralLedgerSort,
	readGeneralLedgerUrlState,
	readMoneyId,
	readMoneyPage
} from './global-money-state.ts';

describe('global money tab compatibility', () => {
	it('maps supported tabs and legacy ledger links to canonical tabs', () => {
		assert.equal(normalizeMoneyTab('ledger'), 'activity');
		assert.equal(normalizeMoneyTab('general-ledger'), 'general-ledger');
		assert.equal(normalizeMoneyTab('cash-flow'), 'cash-flow');
		assert.equal(normalizeMoneyTab('rent-payments'), 'rent-payments');
		assert.equal(normalizeMoneyTab('overview'), 'overview');
		assert.equal(normalizeMoneyTab('reports'), 'reports');
		assert.equal(normalizeMoneyTab('unknown'), 'overview');
	});

	it('accepts old visible-tab names without changing the canonical surface', () => {
		assert.equal(normalizeMoneyTab('history'), 'activity');
		assert.equal(normalizeMoneyTab('summary'), 'overview');
		assert.equal(normalizeMoneyTab('  REPORTS '), 'reports');
	});
});

describe('global money URL filters', () => {
	it('rejects malformed ids and pages', () => {
		const params = new URLSearchParams('account=nope&property=0&unit=12.5&page=-3');
		assert.equal(readMoneyId(params, 'account'), null);
		assert.equal(readMoneyId(params, 'property'), null);
		assert.equal(readMoneyId(params, 'unit'), null);
		assert.equal(readMoneyPage(params), 1);
	});

	it('keeps the deterministic GL default when the URL sort is not allowlisted', () => {
		assert.equal(readGeneralLedgerSort(new URLSearchParams('sort=-effectiveOn,-postedAtUtc,-id')), GENERAL_LEDGER_DEFAULT_SORT);
		assert.equal(readGeneralLedgerSort(new URLSearchParams('sort=postedAtUtc')), 'postedAtUtc');
	});

	it('round-trips general-ledger filters and preserves unrelated params', () => {
		const state = readGeneralLedgerUrlState(
			new URLSearchParams('account=1000&property=8&unit=12&source=TenantReceipt&from=2027-02-01&to=2027-02-28&q=rent&page=2&sort=-accountCode')
		);
		const query = buildMoneyUrlQuery(
			{ tab: 'general-ledger', ...generalLedgerUrlValues(state) },
			{ tab: 'overview', page: 1 },
			new URLSearchParams('coach=accounting')
		);
		const result = new URLSearchParams(query);
		assert.equal(result.get('coach'), 'accounting');
		assert.equal(result.get('tab'), 'general-ledger');
		assert.equal(result.get('account'), '1000');
		assert.equal(result.get('property'), '8');
		assert.equal(result.get('source'), 'TenantReceipt');
		assert.equal(result.get('sort'), '-accountCode');
		assert.equal(result.get('page'), '2');
	});
});
