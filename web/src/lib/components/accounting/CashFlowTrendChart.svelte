<script lang="ts">
	import {
		cashFlowBarPixels,
		cashFlowNetPositionPercent,
		type CashFlowChartPoint
	} from '$lib/accounting/cash-flow-state';
	import { formatAccountingCurrency } from '$lib/accounting/accounting-display';

	interface Props {
		points: readonly CashFlowChartPoint[];
		scale: number;
		advanced?: boolean;
	}

	let { points, scale, advanced = false }: Props = $props();

	const CHART_WIDTH = 960;
	const CHART_HEIGHT = 320;
	const PLOT_LEFT = 100;
	const PLOT_RIGHT = 20;
	const PLOT_TOP = 28;
	const PLOT_BOTTOM = 262;
	const PLOT_HEIGHT = PLOT_BOTTOM - PLOT_TOP;
	const HALF_PLOT_HEIGHT = PLOT_HEIGHT / 2;
	const BASELINE = PLOT_TOP + HALF_PLOT_HEIGHT;
	const PLOT_WIDTH = CHART_WIDTH - PLOT_LEFT - PLOT_RIGHT;

	function formatOutflow(value: number): string {
		const formatted = formatAccountingCurrency(value);
		if (formatted === '—' || value < 0) return formatted;
		return `−${formatted}`;
	}

	function pointSpacing(): number {
		return PLOT_WIDTH / Math.max(points.length, 1);
	}

	function pointX(index: number): number {
		return PLOT_LEFT + pointSpacing() * (index + 0.5);
	}

	function barWidth(): number {
		return Math.min(18, Math.max(8, pointSpacing() * 0.18));
	}

	function barGap(): number {
		return Math.max(3, Math.min(6, barWidth() * 0.3));
	}

	function barHeight(value: number): number {
		return cashFlowBarPixels(value, scale, HALF_PLOT_HEIGHT);
	}

	function netPositionY(value: number): number {
		const positionPercent = cashFlowNetPositionPercent(value, scale);
		return PLOT_BOTTOM - (positionPercent / 100) * PLOT_HEIGHT;
	}

	function monthAriaLabel(point: CashFlowChartPoint): string {
		return `${point.label}: ${advanced ? 'income' : 'money in'} ${formatAccountingCurrency(point.income)}, ${advanced ? 'operating expenses' : 'operating costs'} ${formatOutflow(point.operatingExpenses)}, ${advanced ? 'debt service' : 'loan payments'} ${formatOutflow(point.debtService)}, ${advanced ? 'net cash flow' : 'cash flow'} ${formatAccountingCurrency(point.cashFlow)}`;
	}

	function gridTemplateColumns(): string {
		const leading = (PLOT_LEFT / CHART_WIDTH) * 100;
		const trailing = (PLOT_RIGHT / CHART_WIDTH) * 100;
		const month = (100 - leading - trailing) / Math.max(points.length, 1);
		return `${leading}% repeat(${points.length}, ${month}%) ${trailing}%`;
	}

	function axisTicks(): ReadonlyArray<{ key: string; value: number; y: number }> {
		return [
			{ key: 'max', value: scale, y: PLOT_TOP },
			{ key: 'zero', value: 0, y: BASELINE },
			{ key: 'min', value: -scale, y: PLOT_BOTTOM }
		];
	}
</script>


<div class="overflow-x-auto px-4 py-5 sm:px-5" data-testid="cash-flow-chart-scroll-region">
	<div class="min-w-[56rem]" data-testid="cash-flow-chart-canvas">
		<svg
			class="h-auto w-full overflow-visible"
			viewBox={`0 0 ${CHART_WIDTH} ${CHART_HEIGHT}`}
			role="img"
			aria-labelledby="cash-flow-chart-title cash-flow-chart-description"
			data-testid="cash-flow-trend-svg"
		>
			<title id="cash-flow-chart-title" data-testid="cash-flow-chart-title">Monthly cash flow trend</title>
			<desc id="cash-flow-chart-description" data-testid="cash-flow-chart-description">Money in rises above one shared zero baseline. Operating costs and loan payments fall below it. Net cash flow markers use the same dollar scale.</desc>

			{#each axisTicks() as tick (tick.key)}
				<line
					x1={PLOT_LEFT}
					x2={CHART_WIDTH - PLOT_RIGHT}
					y1={tick.y}
					y2={tick.y}
					stroke="currentColor"
					stroke-width={tick.key === 'zero' ? 1.5 : 1}
					stroke-dasharray={tick.key === 'zero' ? '5 4' : '2 5'}
					class={tick.key === 'zero' ? 'text-border' : 'text-border/50'}
					data-testid={`cash-flow-chart-gridline-${tick.key}`}
				/>
				<text
					x={PLOT_LEFT - 10}
					y={tick.y + 4}
					text-anchor="end"
					fill="currentColor"
					class="font-mono text-[10px] tabular-nums text-muted-foreground"
					data-testid={`cash-flow-chart-axis-label-${tick.key}`}
				>
					{formatAccountingCurrency(tick.value)}
				</text>
			{/each}

			<line
				x1={PLOT_LEFT}
				x2={PLOT_LEFT}
				y1={PLOT_TOP}
				y2={PLOT_BOTTOM}
				stroke="currentColor"
				class="text-border/50"
				data-testid="cash-flow-chart-y-axis"
			/>

			{#each points as point, index (point.key)}
				{@const x = pointX(index)}
				{@const width = barWidth()}
				{@const gap = barGap()}
				{@const incomeHeight = barHeight(point.income)}
				{@const costHeight = barHeight(point.operatingExpenses)}
				{@const loanHeight = barHeight(point.debtService)}
				{@const netY = netPositionY(point.cashFlow)}
				<g
					role="group"
					aria-label={monthAriaLabel(point)}
					data-testid={`cash-flow-month-${point.key}`}
					data-month={point.label}
					data-scale={scale}
				>
					<title data-testid={`cash-flow-month-title-${point.key}`}>{monthAriaLabel(point)}</title>

					<rect
						x={x - width / 2}
						y={BASELINE - incomeHeight}
						width={width}
						height={incomeHeight}
						rx={2}
						class="fill-emerald-500"
						role="img"
						aria-label={`${point.label} ${advanced ? 'income' : 'money in'} ${formatAccountingCurrency(point.income)}`}
						data-testid={`cash-flow-bar-income-${point.key}`}
						data-series="income"
						data-value={point.income}
						data-scaled-height={incomeHeight}
						data-scaled-position={BASELINE - incomeHeight}
					>
						<title>{`${advanced ? 'Income' : 'Money in'} ${formatAccountingCurrency(point.income)}`}</title>
					</rect>

					<rect
						x={x - width - gap / 2}
						y={BASELINE}
						width={width}
						height={costHeight}
						rx={2}
						class="fill-amber-500"
						role="img"
						aria-label={`${point.label} ${advanced ? 'operating expenses' : 'operating costs'} ${formatOutflow(point.operatingExpenses)}`}
						data-testid={`cash-flow-bar-costs-${point.key}`}
						data-series="operating-costs"
						data-value={point.operatingExpenses}
						data-scaled-height={costHeight}
						data-scaled-position={BASELINE}
					>
						<title>{`${advanced ? 'Operating expenses' : 'Operating costs'} ${formatOutflow(point.operatingExpenses)}`}</title>
					</rect>

					<rect
						x={x + gap / 2}
						y={BASELINE}
						width={width}
						height={loanHeight}
						rx={2}
						class="fill-orange-500"
						role="img"
						aria-label={`${point.label} ${advanced ? 'debt service' : 'loan payments'} ${formatOutflow(point.debtService)}`}
						data-testid={`cash-flow-bar-loans-${point.key}`}
						data-series="loan-payments"
						data-value={point.debtService}
						data-scaled-height={loanHeight}
						data-scaled-position={BASELINE}
					>
						<title>{`${advanced ? 'Debt service' : 'Loan payments'} ${formatOutflow(point.debtService)}`}</title>
					</rect>

					<circle
						cx={x}
						cy={netY}
						r={5}
						class="fill-primary stroke-card"
						stroke-width={2}
						role="img"
						aria-label={`${point.label} ${advanced ? 'net cash flow' : 'cash flow'} ${formatAccountingCurrency(point.cashFlow)}`}
						data-testid={`cash-flow-marker-net-${point.key}`}
						data-series="net"
						data-value={point.cashFlow}
						data-scaled-position={netY}
					>
						<title>{`${advanced ? 'Net cash flow' : 'Cash flow'} ${formatAccountingCurrency(point.cashFlow)}`}</title>
					</circle>
				</g>
			{/each}
		</svg>

		<div
			class="grid gap-y-0.5"
			style={`grid-template-columns: ${gridTemplateColumns()}`}
			data-testid="cash-flow-month-details"
		>
			<span aria-hidden="true" data-testid="cash-flow-month-details-leading"></span>
			{#each points as point (point.key)}
				<div class="min-w-0 text-center text-[11px] leading-tight text-muted-foreground" data-testid={`cash-flow-month-details-${point.key}`}>
					<p class="mb-1 whitespace-nowrap text-xs font-medium text-foreground" data-testid={`cash-flow-month-label-${point.key}`}>{point.label}</p>
					<p class="whitespace-nowrap" data-testid={`cash-flow-month-in-${point.key}`}>In {formatAccountingCurrency(point.income)}</p>
					<p class="whitespace-nowrap" data-testid={`cash-flow-month-costs-${point.key}`}>Costs {formatOutflow(point.operatingExpenses)}</p>
					<p class="whitespace-nowrap" data-testid={`cash-flow-month-loans-${point.key}`}>Loans {formatOutflow(point.debtService)}</p>
					<p class="whitespace-nowrap font-medium text-foreground" data-testid={`cash-flow-month-net-${point.key}`}>Net {formatAccountingCurrency(point.cashFlow)}</p>
				</div>
			{/each}
			<span aria-hidden="true" data-testid="cash-flow-month-details-trailing"></span>
		</div>
	</div>
</div>
