import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type { PropertyCashFlow } from '$lib/api/endpoints/cash-flow';
import {
	buildCashFlowChartPoints,
	buildServerCashFlowChartPoints,
	buildCashFlowMonthlyRanges,
	cashFlowBarPercent,
	cashFlowChartScale,
	cashFlowNetPositionPercent,
	cashFlowPropertyDetails,
	resolveCashFlowRange,
	sortCashFlowProperties
} from './cash-flow-state.ts';

const NOW = new Date('2027-02-14T12:00:00Z');

function property(overrides: Partial<PropertyCashFlow>): PropertyCashFlow {
	return {
		propertyId: 1,
		propertyName: 'Maple Street',
		income: 8400,
		operatingExpenses: 2100,
		noi: 6300,
		debtService: 2400,
		cashFlow: 3900,
		...overrides
	};
}

describe('cash-flow period state', () => {
	it('resolves all fixed presets as date-only API ranges', () => {
		assert.deepEqual(resolveCashFlowRange('thisMonth', NOW), {
			from: '2027-02-01',
			to: '2027-02-14'
		});
		assert.deepEqual(resolveCashFlowRange('lastMonth', NOW), {
			from: '2027-01-01',
			to: '2027-01-31'
		});
		assert.deepEqual(resolveCashFlowRange('ytd', NOW), {
			from: '2027-01-01',
			to: '2027-02-14'
		});
		assert.deepEqual(resolveCashFlowRange('trailing12', NOW), {
			from: '2026-03-01',
			to: '2027-02-14'
		});
	});

	it('accepts only complete ordered custom ranges', () => {
		assert.deepEqual(
			resolveCashFlowRange('custom', NOW, { from: '2027-01-10', to: '2027-02-14' }),
			{ from: '2027-01-10', to: '2027-02-14' }
		);
		assert.equal(resolveCashFlowRange('custom', NOW, { from: '2027-02-14' }), null);
		assert.equal(
			resolveCashFlowRange('custom', NOW, { from: '2027-02-15', to: '2027-02-14' }),
			null
		);
	});

	it('clips monthly chart queries to the selected date range', () => {
		assert.deepEqual(
			buildCashFlowMonthlyRanges({ from: '2027-01-15', to: '2027-03-04' }),
			[
				{ key: '2027-01', label: 'Jan 2027', from: '2027-01-15', to: '2027-01-31' },
				{ key: '2027-02', label: 'Feb 2027', from: '2027-02-01', to: '2027-02-28' },
				{ key: '2027-03', label: 'Mar 2027', from: '2027-03-01', to: '2027-03-04' }
			]
		);
	});
});

describe('cash-flow property state', () => {
	it('sorts by server fields without mutating the response rows', () => {
		const rows = [
			property({ propertyId: 2, propertyName: 'Oak Court', cashFlow: 1200 }),
			property({ propertyId: 1, propertyName: 'Maple Street', cashFlow: 3900 })
		];

		assert.deepEqual(
			sortCashFlowProperties(rows).map((row) => row.propertyName),
			['Maple Street', 'Oak Court']
		);
		assert.deepEqual(
			sortCashFlowProperties(rows, 'cashFlow', 'asc').map((row) => row.propertyName),
			['Oak Court', 'Maple Street']
		);
		assert.deepEqual(rows.map((row) => row.propertyName), ['Oak Court', 'Maple Street']);
	});

	it('exposes only the endpoint-provided expense and debt fields for expansion', () => {
		assert.deepEqual(cashFlowPropertyDetails(property({ operatingExpenses: 2100, debtService: 2400 })), [
			{ key: 'operatingExpenses', amount: 2100 },
			{ key: 'debtService', amount: 2400 }
		]);
	});
});

describe('cash-flow chart projection', () => {
	const response = {
		from: '2027-02-01',
		to: '2027-02-28',
		properties: [],
		totalIncome: 24100,
		totalOperatingExpenses: 7860,
		totalNoi: 16240,
		totalDebtService: 8000,
		totalCashFlow: 8240
	};

	it('uses the same server monthly figures as the table totals', () => {
		const withMonths = { ...response, months: [{ month: '2027-02', income: 24100, operatingExpenses: 7860, debtService: 8000, cashFlow: 8240 }] };
		const [point] = buildServerCashFlowChartPoints(withMonths);
		assert.equal(point.income, withMonths.totalIncome);
		assert.equal(point.operatingExpenses, withMonths.totalOperatingExpenses);
		assert.equal(point.debtService, withMonths.totalDebtService);
		assert.equal(point.cashFlow, withMonths.totalCashFlow);
	});

	it('projects each monthly response without recomputing its totals', () => {
		assert.deepEqual(
			buildCashFlowChartPoints([
				{
					range: { key: '2027-02', label: 'Feb 2027', from: response.from, to: response.to },
					response
				}
			]),
			[
				{
					key: '2027-02',
					label: 'Feb 2027',
					income: 24100,
					operatingExpenses: 7860,
					debtService: 8000,
					cashFlow: 8240
				}
			]
		);
	});

	it('uses server values only to scale visual geometry', () => {
		const points = buildCashFlowChartPoints([
			{
				range: { key: '2027-02', label: 'Feb 2027', from: response.from, to: response.to },
				response
			}
		]);
		const scale = cashFlowChartScale(points);
		assert.equal(scale, 24100);
		assert.equal(cashFlowBarPercent(response.totalIncome, scale), 100);
		assert.equal(cashFlowBarPercent(response.totalOperatingExpenses, scale), (7860 / 24100) * 100);
		assert.equal(cashFlowNetPositionPercent(response.totalCashFlow, scale), 50 + (8240 / 24100) * 45);
	});
});
