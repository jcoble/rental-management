import { cleanup, render } from '@testing-library/svelte';
import { afterEach, describe, expect, it } from 'vitest';
import CashFlowTrendChart from '$lib/components/accounting/CashFlowTrendChart.svelte';
import { cashFlowChartScale, type CashFlowChartPoint } from '$lib/accounting/cash-flow-state';

afterEach(() => cleanup());

const points: CashFlowChartPoint[] = [
	{
		key: '2026-01',
		label: 'Jan 2026',
		income: 18055,
		operatingExpenses: 1234,
		debtService: 8766,
		cashFlow: 8055
	},
	{
		key: '2026-02',
		label: 'Feb 2026',
		income: 2,
		operatingExpenses: 12.34,
		debtService: 0,
		cashFlow: -10.34
	},
	{
		key: '2026-03',
		label: 'Mar 2026',
		income: 11426,
		operatingExpenses: 0,
		debtService: 1000,
		cashFlow: 10426
	}
];

describe('cash-flow trend chart rendering', () => {
	it('renders one shared scale with truthful bar and marker geometry', () => {
		const view = render(CashFlowTrendChart, {
			props: { points, scale: cashFlowChartScale(points) }
		});

		const januaryIncome = view.getByTestId('cash-flow-bar-income-2026-01') as SVGRectElement;
		const marchIncome = view.getByTestId('cash-flow-bar-income-2026-03') as SVGRectElement;
		const februaryIncome = view.getByTestId('cash-flow-bar-income-2026-02') as SVGRectElement;
		const januaryMarker = view.getByTestId('cash-flow-marker-net-2026-01') as SVGCircleElement;

		expect(view.getByTestId('cash-flow-month-label-2026-01').textContent).toBe('Jan 2026');
		expect(view.queryByText(/Bars show the direction/)).toBeNull();
		expect(view.getByTestId('cash-flow-month-2026-01').getAttribute('data-scale')).toBe('18055');
		expect(Number(januaryIncome.dataset.scaledHeight)).toBeCloseTo(117, 5);
		expect(Number(marchIncome.dataset.scaledHeight) / Number(januaryIncome.dataset.scaledHeight)).toBeCloseTo(11426 / 18055, 5);
		expect(Number(februaryIncome.dataset.scaledHeight)).toBeGreaterThanOrEqual(2);
		expect(Number(januaryMarker.dataset.scaledPosition)).toBeLessThan(145);
		expect(januaryIncome.dataset.value).toBe('18055');
		expect(view.getByTestId('cash-flow-month-costs-2026-02').textContent).toContain('−$12.34');
	});
});
