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

	type TooltipSeries = 'income' | 'operating-costs' | 'loan-payments' | 'net';

	interface ChartTooltip {
		id: string;
		x: number;
		y: number;
		text: string;
	}

	let { points, scale, advanced = false }: Props = $props();
	let activeTooltip = $state<ChartTooltip | null>(null);
	let hoveredTooltipId = $state<string | null>(null);
	let focusedTooltipId = $state<string | null>(null);

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

	function tooltipSeriesLabel(series: TooltipSeries): string {
		if (series === 'income') return advanced ? 'Income' : 'Money in';
		if (series === 'operating-costs') return advanced ? 'Operating expenses' : 'Operating costs';
		if (series === 'loan-payments') return advanced ? 'Debt service' : 'Loan payments';
		return advanced ? 'Net cash flow' : 'Cash flow';
	}

	function tooltipAmount(series: TooltipSeries, value: number): string {
		return series === 'operating-costs' || series === 'loan-payments'
			? formatOutflow(value)
			: formatAccountingCurrency(value);
	}

	function chartTooltip(
		point: CashFlowChartPoint,
		series: TooltipSeries,
		value: number,
		x: number,
		y: number
	): ChartTooltip {
		return {
			id: `cash-flow-tooltip-${point.key}-${series}`,
			x,
			y,
			text: `${point.label} · ${tooltipSeriesLabel(series)}: ${tooltipAmount(series, value)}`
		};
	}

	function showTooltip(tooltip: ChartTooltip, source: 'hover' | 'focus'): void {
		if (source === 'hover') hoveredTooltipId = tooltip.id;
		else focusedTooltipId = tooltip.id;
		activeTooltip = tooltip;
	}

	function hideTooltip(id: string, source: 'hover' | 'focus'): void {
		if (source === 'hover' && hoveredTooltipId === id) hoveredTooltipId = null;
		if (source === 'focus' && focusedTooltipId === id) focusedTooltipId = null;
		if (activeTooltip?.id === id && hoveredTooltipId !== id && focusedTooltipId !== id) activeTooltip = null;
	}

	function tooltipTop(y: number): number {
		return Math.max(2, Math.min(82, (y / CHART_HEIGHT) * 100 - 8));
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
		<div class="relative" data-testid="cash-flow-chart-plot">
			<svg
				class="h-auto w-full overflow-visible"
				viewBox={`0 0 ${CHART_WIDTH} ${CHART_HEIGHT}`}
				role="group"
				aria-labelledby="cash-flow-chart-title"
				aria-describedby="cash-flow-chart-description"
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
				{@const incomeHeight = barHeight(point.income)}
				{@const costHeight = barHeight(point.operatingExpenses)}
				{@const loanHeight = barHeight(point.debtService)}
				{@const loanY = BASELINE + costHeight}
				{@const netY = netPositionY(point.cashFlow)}
				{@const incomeTooltip = chartTooltip(point, 'income', point.income, x, BASELINE - incomeHeight)}
				{@const costsTooltip = chartTooltip(point, 'operating-costs', point.operatingExpenses, x, BASELINE + costHeight / 2)}
				{@const loansTooltip = chartTooltip(point, 'loan-payments', point.debtService, x, loanY + loanHeight / 2)}
				{@const netTooltip = chartTooltip(point, 'net', point.cashFlow, x, netY)}
				<g
					role="group"
					aria-label={monthAriaLabel(point)}
					data-testid={`cash-flow-month-${point.key}`}
					data-month={point.label}
					data-scale={scale}
				>
					<title data-testid={`cash-flow-month-title-${point.key}`}>{monthAriaLabel(point)}</title>

					<!-- svelte-ignore a11y_no_noninteractive_tabindex -- focus exposes the mark's tooltip without making it an action control. -->
					<rect
						x={x - width / 2}
						y={BASELINE - incomeHeight}
						width={width}
						height={incomeHeight}
						rx={2}
						class="cash-flow-mark fill-emerald-500"
						role="img"
						aria-label={`${point.label} ${advanced ? 'income' : 'money in'} ${formatAccountingCurrency(point.income)}`}
						aria-describedby={activeTooltip?.id === incomeTooltip.id ? 'cash-flow-chart-tooltip' : undefined}
						tabindex="0"
						focusable="true"
						data-testid={`cash-flow-bar-income-${point.key}`}
						data-series="income"
						data-value={point.income}
						data-scaled-height={incomeHeight}
						data-scaled-position={BASELINE - incomeHeight}
						onmouseenter={() => showTooltip(incomeTooltip, 'hover')}
						onmouseleave={() => hideTooltip(incomeTooltip.id, 'hover')}
						onfocus={() => showTooltip(incomeTooltip, 'focus')}
						onblur={() => hideTooltip(incomeTooltip.id, 'focus')}
					>
						<title>{`${advanced ? 'Income' : 'Money in'} ${formatAccountingCurrency(point.income)}`}</title>
					</rect>

					<!-- svelte-ignore a11y_no_noninteractive_tabindex -- focus exposes the mark's tooltip without making it an action control. -->
					<rect
						x={x - width / 2}
						y={BASELINE}
						width={width}
						height={costHeight}
						rx={2}
						class="cash-flow-mark fill-amber-500"
						role="img"
						aria-label={`${point.label} ${advanced ? 'operating expenses' : 'operating costs'} ${formatOutflow(point.operatingExpenses)}`}
						aria-describedby={activeTooltip?.id === costsTooltip.id ? 'cash-flow-chart-tooltip' : undefined}
						tabindex="0"
						focusable="true"
						data-testid={`cash-flow-bar-costs-${point.key}`}
						data-series="operating-costs"
						data-value={point.operatingExpenses}
						data-scaled-height={costHeight}
						data-scaled-position={BASELINE}
						onmouseenter={() => showTooltip(costsTooltip, 'hover')}
						onmouseleave={() => hideTooltip(costsTooltip.id, 'hover')}
						onfocus={() => showTooltip(costsTooltip, 'focus')}
						onblur={() => hideTooltip(costsTooltip.id, 'focus')}
					>
						<title>{`${advanced ? 'Operating expenses' : 'Operating costs'} ${formatOutflow(point.operatingExpenses)}`}</title>
					</rect>

					<!-- svelte-ignore a11y_no_noninteractive_tabindex -- focus exposes the mark's tooltip without making it an action control. -->
					<rect
						x={x - width / 2}
						y={loanY}
						width={width}
						height={loanHeight}
						rx={2}
						class="cash-flow-mark fill-orange-500"
						role="img"
						aria-label={`${point.label} ${advanced ? 'debt service' : 'loan payments'} ${formatOutflow(point.debtService)}`}
						aria-describedby={activeTooltip?.id === loansTooltip.id ? 'cash-flow-chart-tooltip' : undefined}
						tabindex="0"
						focusable="true"
						data-testid={`cash-flow-bar-loans-${point.key}`}
						data-series="loan-payments"
						data-value={point.debtService}
						data-scaled-height={loanHeight}
						data-scaled-position={loanY}
						onmouseenter={() => showTooltip(loansTooltip, 'hover')}
						onmouseleave={() => hideTooltip(loansTooltip.id, 'hover')}
						onfocus={() => showTooltip(loansTooltip, 'focus')}
						onblur={() => hideTooltip(loansTooltip.id, 'focus')}
					>
						<title>{`${advanced ? 'Debt service' : 'Loan payments'} ${formatOutflow(point.debtService)}`}</title>
					</rect>

					<!-- svelte-ignore a11y_no_noninteractive_tabindex -- focus exposes the mark's tooltip without making it an action control. -->
					<circle
						cx={x}
						cy={netY}
						r={5}
						class="cash-flow-mark fill-primary stroke-card"
						stroke-width={2}
						role="img"
						aria-label={`${point.label} ${advanced ? 'net cash flow' : 'cash flow'} ${formatAccountingCurrency(point.cashFlow)}`}
						aria-describedby={activeTooltip?.id === netTooltip.id ? 'cash-flow-chart-tooltip' : undefined}
						tabindex="0"
						focusable="true"
						data-testid={`cash-flow-marker-net-${point.key}`}
						data-series="net"
						data-value={point.cashFlow}
						data-scaled-position={netY}
						onmouseenter={() => showTooltip(netTooltip, 'hover')}
						onmouseleave={() => hideTooltip(netTooltip.id, 'hover')}
						onfocus={() => showTooltip(netTooltip, 'focus')}
						onblur={() => hideTooltip(netTooltip.id, 'focus')}
					>
						<title>{`${advanced ? 'Net cash flow' : 'Cash flow'} ${formatAccountingCurrency(point.cashFlow)}`}</title>
					</circle>
				</g>
			{/each}
			</svg>

			{#if activeTooltip}
				<div
					id="cash-flow-chart-tooltip"
					role="tooltip"
					class="cash-flow-chart-tooltip pointer-events-none absolute z-10 -translate-x-1/2 whitespace-nowrap rounded-md border border-border bg-popover px-2.5 py-1.5 text-xs font-medium text-popover-foreground shadow-md"
					style={`left: ${(activeTooltip.x / CHART_WIDTH) * 100}%; top: ${tooltipTop(activeTooltip.y)}%;`}
					data-testid="cash-flow-chart-tooltip"
				>
					{activeTooltip.text}
				</div>
			{/if}
		</div>

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

<style>
	.cash-flow-mark {
		cursor: default;
		outline: none;
	}

	.cash-flow-mark:focus-visible {
		stroke: var(--ring, currentColor);
		stroke-width: 3px;
		paint-order: stroke;
	}
</style>
