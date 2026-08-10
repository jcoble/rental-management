import { cleanup, fireEvent, render } from '@testing-library/svelte';
import { afterEach, describe, expect, it } from 'vitest';
import CashFlowTrendChart from '$lib/components/accounting/CashFlowTrendChart.svelte';
import { formatAccountingCurrency } from '$lib/accounting/accounting-display';
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

const stackedOutflowPoints: CashFlowChartPoint[] = [
	{
		key: '2026-04',
		label: 'Apr 2026',
		income: 100,
		operatingExpenses: 80,
		debtService: 80,
		cashFlow: -60
	}
];

const clampedStackOutflowPoints: CashFlowChartPoint[] = [
	{
		key: '2026-05',
		label: 'May 2026',
		income: 99,
		operatingExpenses: 100,
		debtService: 0.01,
		cashFlow: -1.01
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

		for (const point of points) {
			const marker = view.getByTestId(`cash-flow-marker-net-${point.key}`) as SVGCircleElement;
			const markerCenter = Number(marker.getAttribute('cx'));
			for (const series of ['income', 'costs', 'loans'] as const) {
				const rect = view.getByTestId(`cash-flow-bar-${series === 'income' ? 'income' : series}-${point.key}`) as SVGRectElement;
				const rectCenter = Number(rect.getAttribute('x')) + Number(rect.getAttribute('width')) / 2;
				expect(rectCenter).toBeCloseTo(markerCenter, 8);
			}

			const costs = view.getByTestId(`cash-flow-bar-costs-${point.key}`) as SVGRectElement;
			const loans = view.getByTestId(`cash-flow-bar-loans-${point.key}`) as SVGRectElement;
			expect(Number(loans.getAttribute('y'))).toBeCloseTo(
				Number(costs.getAttribute('y')) + Number(costs.getAttribute('height')),
				8
			);
		}
	});

	it('keeps a stacked outflow within the shared bottom-axis scale', () => {
		const scale = cashFlowChartScale(stackedOutflowPoints);
		const view = render(CashFlowTrendChart, { props: { points: stackedOutflowPoints, scale } });
		const costs = view.getByTestId('cash-flow-bar-costs-2026-04') as SVGRectElement;
		const loans = view.getByTestId('cash-flow-bar-loans-2026-04') as SVGRectElement;
		const finalOutflowEdge = Number(loans.getAttribute('y')) + Number(loans.getAttribute('height'));
		const bottomAxis = view.getByTestId('cash-flow-chart-gridline-min');

		expect(scale).toBe(160);
		expect(view.getByTestId('cash-flow-chart-axis-label-min').textContent).toBe('-$160.00');
		expect(finalOutflowEdge).toBeLessThanOrEqual(Number(bottomAxis.getAttribute('y1')));
		expect(finalOutflowEdge).toBeCloseTo(Number(bottomAxis.getAttribute('y1')), 8);
		expect(finalOutflowEdge).toBeCloseTo(
			Number(costs.getAttribute('y')) + Number(costs.getAttribute('height')) + Number(loans.getAttribute('height')),
			8
		);
	});

	it('fits a minimum-clamped stack to the bottom axis when the stack sets the scale', () => {
		const scale = cashFlowChartScale(clampedStackOutflowPoints);
		const view = render(CashFlowTrendChart, { props: { points: clampedStackOutflowPoints, scale } });
		const costs = view.getByTestId('cash-flow-bar-costs-2026-05') as SVGRectElement;
		const loans = view.getByTestId('cash-flow-bar-loans-2026-05') as SVGRectElement;
		const finalOutflowEdge = Number(loans.getAttribute('y')) + Number(loans.getAttribute('height'));
		const bottomAxis = view.getByTestId('cash-flow-chart-gridline-min');
		const bottomAxisY = Number(bottomAxis.getAttribute('y1'));

		expect(scale).toBe(100.01);
		expect(view.getByTestId('cash-flow-chart-axis-label-min').textContent).toBe('-$100.01');
		expect(Number(loans.dataset.scaledHeight)).toBeCloseTo((0.01 / 100.01) * 117, 8);
		expect(Number(loans.dataset.scaledHeight)).toBeLessThan(2);
		expect(finalOutflowEdge).toBeLessThanOrEqual(bottomAxisY);
		expect(finalOutflowEdge).toBeCloseTo(bottomAxisY, 8);
		expect(finalOutflowEdge).toBeCloseTo(
			Number(costs.getAttribute('y')) + Number(costs.getAttribute('height')) + Number(loans.getAttribute('height')),
			8
		);
	});

	it('shows each exact series value through the keyboard-focus tooltip', async () => {
		const view = render(CashFlowTrendChart, {
			props: { points, scale: cashFlowChartScale(points) }
		});

		const outflow = (value: number): string => {
			const formatted = formatAccountingCurrency(value);
			return value < 0 ? formatted : `−${formatted}`;
		};
		const expectedMarks = points.flatMap((point) => [
			[`cash-flow-bar-income-${point.key}`, `${point.label} · Money in: ${formatAccountingCurrency(point.income)}`],
			[`cash-flow-bar-costs-${point.key}`, `${point.label} · Operating costs: ${outflow(point.operatingExpenses)}`],
			[`cash-flow-bar-loans-${point.key}`, `${point.label} · Loan payments: ${outflow(point.debtService)}`],
			[`cash-flow-marker-net-${point.key}`, `${point.label} · Cash flow: ${formatAccountingCurrency(point.cashFlow)}`]
		] as const);
		const hoverMark = view.getByTestId('cash-flow-bar-income-2026-01');
		await fireEvent.mouseEnter(hoverMark);
		expect(view.getByTestId('cash-flow-chart-tooltip').textContent).toContain(expectedMarks[0][1]);
		await fireEvent.mouseLeave(hoverMark);

		for (const [testId, expectedText] of expectedMarks) {
			const mark = view.getByTestId(testId) as SVGGraphicsElement;
			expect(mark.getAttribute('tabindex')).toBe('0');
			expect(mark.getAttribute('role')).toBe('img');
			expect(view.getByRole('img', { name: mark.getAttribute('aria-label') ?? '' })).toBe(mark);
			await fireEvent.focus(mark);
			expect(view.getByTestId('cash-flow-chart-tooltip').textContent).toContain(expectedText);
		}
		expect(view.getByTestId('cash-flow-trend-svg').getAttribute('role')).toBe('group');
		expect(view.queryAllByRole('button')).toHaveLength(0);

		await fireEvent.blur(view.getByTestId('cash-flow-marker-net-2026-03'));
		expect(view.queryByTestId('cash-flow-chart-tooltip')).toBeNull();
	});
});
