import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	ACCOUNTING_TABS,
	defaultTabForMode,
	groupReports,
	resolveAccountingTab,
	visibleAccountingTabs
} from './simple-mode-surface.ts';

describe('accounting tabs by detail level', () => {
	it('keeps Simple to at most four tabs and hides the general ledger', () => {
		const tabs = visibleAccountingTabs('simple');
		assert.ok(tabs.length <= 4, `Simple should show at most 4 tabs, got ${tabs.length}`);
		assert.ok(!tabs.some((tab) => tab.value === 'general-ledger'));
		assert.ok(tabs.every((tab) => tab.label.length > 0));
	});

	it('shows every tab in Advanced', () => {
		assert.deepEqual(visibleAccountingTabs('advanced'), [...ACCOUNTING_TABS]);
		assert.ok(visibleAccountingTabs('advanced').some((tab) => tab.value === 'general-ledger'));
	});

	it('lands Simple on rent and payments and leaves Advanced on the overview', () => {
		assert.equal(defaultTabForMode('simple'), 'rent-payments');
		assert.equal(defaultTabForMode('advanced'), 'overview');
		assert.ok(visibleAccountingTabs('simple').some((tab) => tab.value === 'rent-payments'));
	});

	it('sends a link to a hidden tab back to the tab this level lands on', () => {
		assert.equal(resolveAccountingTab('general-ledger', 'simple'), 'rent-payments');
		assert.equal(resolveAccountingTab('general-ledger', 'advanced'), 'general-ledger');
		assert.equal(resolveAccountingTab('activity', 'simple'), 'activity');
		assert.equal(resolveAccountingTab(null, 'simple'), 'rent-payments');
		assert.equal(resolveAccountingTab(null, 'advanced'), 'overview');
	});
});

describe('report grouping', () => {
	const catalog = {
		categories: [
			{
				reports: [
					{ key: 'income-expense-statement' },
					{ key: 'accounting-balance-sheet' },
					{ key: 'schedule-e' }
				]
			},
			{ reports: [{ key: 'rent-roll' }, { key: 'delinquency' }, { key: 'rent-ledger' }] }
		]
	};

	it('puts the three everyday reports first, in reading order', () => {
		assert.deepEqual(
			groupReports(catalog).everyday.map((report) => report.key),
			['rent-roll', 'delinquency', 'income-expense-statement']
		);
	});

	it('keeps every other report in the accountant group', () => {
		assert.deepEqual(
			groupReports(catalog).accountant.map((report) => report.key),
			['accounting-balance-sheet', 'schedule-e', 'rent-ledger']
		);
	});

	it('handles an empty or missing catalog', () => {
		assert.deepEqual(groupReports(null), { everyday: [], accountant: [] });
		assert.deepEqual(groupReports({ categories: [] }), { everyday: [], accountant: [] });
	});
});
