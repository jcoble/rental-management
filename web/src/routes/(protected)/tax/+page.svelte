<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import {
		accounting,
		downloadScheduleECsv,
		downloadYearEndPacket
	} from '$lib/api/endpoints/accounting';
	import type { ScheduleEReport } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showError } from '$lib/utils/toast';
	import * as Card from '$lib/components/ui/card';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { FileText, Receipt } from '@lucide/svelte';

	const CURRENT_YEAR = new Date().getFullYear();
	const YEAR_OPTIONS = Array.from({ length: 5 }, (_, i) => CURRENT_YEAR - i);
	// Default the year-end packet to the previous calendar year — the filing year.
	const PACKET_YEAR_OPTIONS = Array.from({ length: 5 }, (_, i) => CURRENT_YEAR - 1 - i);

	const portfolioId = $derived(getCurrentPortfolioId());
	let selectedYear = $state(String(CURRENT_YEAR));
	let downloading = $state(false);
	let packetYear = $state(String(CURRENT_YEAR - 1));
	let downloadingPacket = $state(false);

	const scheduleEQuery = createQuery(() => ({
		queryKey: ['schedule-e', portfolioId, selectedYear],
		queryFn: () => accounting.scheduleE(Number(selectedYear)),
		enabled: !!portfolioId
	}));

	const report = $derived(scheduleEQuery.data as ScheduleEReport | undefined);

	function money(value: number) {
		return new Intl.NumberFormat('en-US', {
			style: 'currency',
			currency: 'USD',
			maximumFractionDigits: 0
		}).format(value || 0);
	}

	async function handleDownload() {
		downloading = true;
		try {
			await downloadScheduleECsv(Number(selectedYear));
		} catch {
			showError('Could not download CSV. Please try again.');
		} finally {
			downloading = false;
		}
	}

	async function handlePacketDownload() {
		downloadingPacket = true;
		try {
			await downloadYearEndPacket(Number(packetYear));
		} catch {
			showError('Could not download the year-end packet. Please try again.');
		} finally {
			downloadingPacket = false;
		}
	}
</script>

<svelte:head>
	<title>Tax Summary - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="tax-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-4">
		<div>
			<h1 class="flex items-center gap-2 text-2xl font-bold">
				<Receipt class="h-6 w-6 text-primary" />
				Tax summary (Schedule E)
			</h1>
			<p class="mt-1 text-sm text-muted-foreground">
				Estimated rental income and deductible expenses by property — for reference only,
				not tax advice. Share with your accountant.
			</p>
		</div>
		<div class="flex items-center gap-2">
			<Select.Root type="single" bind:value={selectedYear}>
				<Select.Trigger class="w-28" data-testid="tax-year-select">
					{selectedYear}
				</Select.Trigger>
				<Select.Content>
					{#each YEAR_OPTIONS as year}
						<Select.Item value={String(year)} label={String(year)}>{year}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<Button
				variant="outline"
				onclick={handleDownload}
				disabled={downloading || !report}
				data-testid="tax-download-csv"
			>
				{downloading ? 'Downloading…' : 'Download CSV'}
			</Button>
		</div>
	</div>

	<!-- Year-end packet: a single PDF to hand the accountant -->
	<Card.Root class="mb-6 gap-0 py-0" data-testid="year-end-packet">
		<Card.Content class="p-4">
			<div class="flex flex-wrap items-start justify-between gap-4">
				<div class="flex items-start gap-3">
					<FileText class="mt-0.5 h-5 w-5 shrink-0 text-primary" />
					<div>
						<p class="font-semibold">Year-end packet (PDF)</p>
						<p class="mt-1 max-w-xl text-sm text-muted-foreground">
							A single PDF to hand your accountant: Schedule E summary, per-property P&amp;L, cash
							flow, and rent roll.
						</p>
					</div>
				</div>
				<div class="flex items-center gap-2">
					<Select.Root type="single" bind:value={packetYear}>
						<Select.Trigger class="w-28" data-testid="packet-year-select">
							{packetYear}
						</Select.Trigger>
						<Select.Content>
							{#each PACKET_YEAR_OPTIONS as year}
								<Select.Item value={String(year)} label={String(year)}>{year}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
					<Button
						variant="outline"
						onclick={handlePacketDownload}
						disabled={downloadingPacket}
						data-testid="packet-download-pdf"
					>
						{downloadingPacket ? 'Preparing…' : 'Download packet'}
					</Button>
				</div>
			</div>
		</Card.Content>
	</Card.Root>

	{#if scheduleEQuery.isLoading}
		<p class="py-12 text-center text-sm text-muted-foreground" data-testid="tax-loading">
			Loading tax summary…
		</p>
	{:else if scheduleEQuery.isError}
		<p class="py-12 text-center text-sm text-destructive" data-testid="tax-error">
			Could not load tax data. Please try again.
		</p>
	{:else if !report || report.properties.length === 0}
		<p class="py-12 text-center text-sm text-muted-foreground" data-testid="tax-empty">
			No rental income or expense data for {selectedYear}.
		</p>
	{:else}
		<!-- Grand-total summary cards -->
		<div class="mb-6 grid gap-4 sm:grid-cols-3" data-testid="tax-summary-cards">
			<Card.Root class="gap-0 py-0" data-testid="tax-total-income">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Total rental income</p>
					<p class="text-2xl font-bold text-green-600">{money(report.totalRentalIncome)}</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="gap-0 py-0" data-testid="tax-total-expenses">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Total expenses</p>
					<p class="text-2xl font-bold text-destructive">{money(report.totalExpenses)}</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="gap-0 py-0" data-testid="tax-net-income">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Net income</p>
					<p
						class="text-2xl font-bold {report.netIncome >= 0
							? 'text-green-600'
							: 'text-destructive'}"
					>
						{money(report.netIncome)}
					</p>
				</Card.Content>
			</Card.Root>
		</div>

		<!-- Per-property breakdown -->
		<div class="space-y-4" data-testid="tax-properties">
			{#each report.properties as property (property.propertyId)}
				<Card.Root class="gap-0 py-0" data-testid="tax-property-{property.propertyId}">
					<Card.Header
						class="border-b border-border px-4 py-3"
					>
						<Card.Title class="text-base font-semibold">{property.propertyName}</Card.Title>
					</Card.Header>
					<Card.Content class="p-4">
						<div class="mb-3 grid gap-3 sm:grid-cols-3">
							<div>
								<p class="text-xs text-muted-foreground">Rental income</p>
								<p class="text-lg font-semibold text-green-600">{money(property.rentalIncome)}</p>
							</div>
							<div>
								<p class="text-xs text-muted-foreground">Total expenses</p>
								<p class="text-lg font-semibold text-destructive">{money(property.totalExpenses)}</p>
							</div>
							<div>
								<p class="text-xs text-muted-foreground">Net income</p>
								<p
									class="text-lg font-semibold {property.netIncome >= 0
										? 'text-green-600'
										: 'text-destructive'}"
								>
									{money(property.netIncome)}
								</p>
							</div>
						</div>

						{#if property.expensesByCategory.length > 0}
							<div class="mt-3 border-t border-border pt-3">
								<p class="mb-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">
									Expenses by category
								</p>
								<div class="overflow-x-auto">
									<table class="w-full text-sm" data-testid="tax-expenses-table-{property.propertyId}">
										<thead>
											<tr class="border-b border-border">
												<th class="py-1.5 text-left font-medium text-muted-foreground">Category</th>
												<th class="py-1.5 text-right font-medium text-muted-foreground">Amount</th>
											</tr>
										</thead>
										<tbody>
											{#each property.expensesByCategory as item (item.category)}
												<tr class="border-b border-border/50 last:border-0">
													<td class="py-1.5 text-foreground">{item.category}</td>
													<td class="py-1.5 text-right tabular-nums">{money(item.amount)}</td>
												</tr>
											{/each}
										</tbody>
									</table>
								</div>
							</div>
						{/if}
					</Card.Content>
				</Card.Root>
			{/each}
		</div>
	{/if}
</div>
