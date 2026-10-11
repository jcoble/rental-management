<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import {
		accounting,
		downloadScheduleECsv,
		downloadYearEndPacket
	} from '$lib/api/endpoints/accounting';
	import { reports as reportsApi } from '$lib/api/endpoints/reports';
	import { vendors } from '$lib/api/endpoints/vendors';
	import type { ScheduleEReport } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { formatExpenseCategory } from '$lib/accounting/expense-categories';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import * as Card from '$lib/components/ui/card';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { FileText, Receipt, MessageSquare, AlertTriangle } from '@lucide/svelte';
	import { ApiError } from '$lib/api/client';
	import { formatAccountingCurrency } from '$lib/accounting/accounting-display';

	const CURRENT_YEAR = new Date().getFullYear();
	const YEAR_OPTIONS = Array.from({ length: 5 }, (_, i) => CURRENT_YEAR - i);

	const portfolioId = $derived(getCurrentPortfolioId());
	let selectedYear = $state(String(CURRENT_YEAR));
	let downloading = $state(false);
	let downloadingPacket = $state(false);

	const scheduleEQuery = createQuery(() => ({
		queryKey: ['schedule-e', portfolioId, selectedYear],
		queryFn: () => accounting.scheduleE(Number(selectedYear)),
		enabled: !!portfolioId
	}));

	const report = $derived(scheduleEQuery.data as ScheduleEReport | undefined);

	// 1099 checklist: 1099-eligible vendors + whether each has a W-9 on file, scoped to the selected
	// tax year. 1099 reporting is strictly per calendar year ($600/payee/year), so this reads the
	// year-scoped /reports/vendor-1099 endpoint (Paid filtered to the year) and keys the query on
	// selectedYear — switching the year re-fetches so the Paid totals and the $600 flag reflect that
	// year alone (not an all-time sum).
	const vendor1099Query = createQuery(() => ({
		queryKey: ['vendor-1099', portfolioId, selectedYear],
		queryFn: () => reportsApi.vendor1099({ year: Number(selectedYear) }),
		enabled: !!portfolioId
	}));

	const vendors1099 = $derived(vendor1099Query.data?.rows ?? []);
	const vendorsNeedingW9 = $derived(vendor1099Query.data?.needsW9Count ?? 0);

	let w9OperationIds = $state<Record<number, string>>({});
	const requestW9Mutation = createMutation(() => ({
		mutationFn: ({ vendorId }: { vendorId: number }) =>
			vendors.requestW9(vendorId, (w9OperationIds[vendorId] ??= crypto.randomUUID())),
		onSuccess: (res, { vendorId }) => {
			delete w9OperationIds[vendorId];
			showSuccess(`W-9 request texted to ${res.sentTo}.`);
		},
		// 400 { error } when the vendor has no phone on file — surface it plainly.
		onError: (err, { vendorId }) => {
			if (err instanceof ApiError && err.status >= 400 && err.status < 500
				&& err.status !== 408 && err.status !== 429) delete w9OperationIds[vendorId];
			showError(apiErrorMessage(err));
		}
	}));

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
			await downloadYearEndPacket(Number(selectedYear));
		} catch {
			showError('Could not download the year-end packet. Please try again.');
		} finally {
			downloadingPacket = false;
		}
	}
</script>

<svelte:head>
	<title>Rental Tax Summary - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="tax-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-4">
		<div>
			<h1 class="flex items-center gap-2 text-2xl font-bold">
				<Receipt class="h-6 w-6 text-primary" />
				Rental tax summary
			</h1>
			<p class="mt-1 text-sm text-muted-foreground">
				Estimate the rental income and expenses your accountant may use for Schedule E.
				This is a reference, not tax advice.
			</p>
			<details class="mt-2 max-w-xl text-xs text-muted-foreground" data-testid="tax-basis-note">
				<summary class="cursor-pointer font-medium">Why totals may differ from owner statements</summary>
				<p class="mt-2">
					This summary counts an expense when it is incurred. The
					<a href="/owners-report" class="underline underline-offset-2">owner statements</a> count it when it is paid,
					so the same property can show a different expense total.
				</p>
			</details>
			<a
				href="/docs/taxes-and-1099"
				class="mt-2 inline-flex min-h-11 items-center text-sm font-medium text-primary underline-offset-4 hover:underline"
				data-testid="tax-help-link"
			>
				How tax reporting works
			</a>
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
					<span class="text-sm text-muted-foreground" data-testid="packet-year-label">
						{selectedYear}
					</span>
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

	<!-- 1099 checklist: who needs a W-9 before you file 1099s -->
	<Card.Root class="mb-6 gap-0 py-0" data-testid="vendors-1099-card">
		<Card.Header class="border-b border-border px-4 py-3">
			<Card.Title class="text-base font-semibold">1099 checklist</Card.Title>
			<p class="mt-1 text-sm text-muted-foreground">
				These vendors need a W-9 before you file 1099s. A W-9 gives you their tax ID. Text a
				request to anyone still missing one.
			</p>
		</Card.Header>
		<Card.Content class="p-4">
			{#if vendor1099Query.isLoading}
				<LoadingState label="Loading 1099 vendors" variant="spinner" testid="vendors-1099-loading" />
			{:else if vendor1099Query.isError}
				<div class="flex flex-wrap items-center justify-center gap-3 py-6" role="alert" data-testid="vendors-1099-error">
					<p class="text-sm text-destructive">Could not load the 1099 checklist.</p>
					<Button size="sm" variant="outline" onclick={() => vendor1099Query.refetch()}>Retry checklist</Button>
				</div>
			{:else if vendors1099.length === 0}
				<p class="py-6 text-center text-sm text-muted-foreground" data-testid="vendors-1099-empty">
					No 1099-eligible vendors yet.
				</p>
			{:else}
				{#if vendorsNeedingW9 > 0}
					<div
						class="m3-warning-surface mb-3 flex items-center gap-2 rounded-md px-3 py-2 text-sm"
						data-testid="vendors-1099-warning"
					>
						<AlertTriangle class="h-4 w-4 shrink-0" />
						<span>{vendorsNeedingW9} vendor{vendorsNeedingW9 === 1 ? '' : 's'} still {vendorsNeedingW9 === 1 ? 'needs' : 'need'} a W-9.</span>
					</div>
				{/if}
				<div class="overflow-x-auto">
					<table class="w-full text-sm" data-testid="vendors-1099-table">
						<thead>
							<tr class="border-b border-border">
								<th class="py-1.5 pr-4 text-left font-medium text-muted-foreground">Vendor</th>
								<th class="py-1.5 pr-6 text-right font-medium text-muted-foreground">Paid</th>
								<th class="py-1.5 pr-4 text-left font-medium text-muted-foreground">W-9</th>
								<th class="py-1.5 text-right font-medium text-muted-foreground">Action</th>
							</tr>
						</thead>
						<tbody>
							{#each vendors1099 as v (v.vendorId)}
								<tr
									class="border-b border-border/50 last:border-0 {v.needsW9 ? 'bg-[color-mix(in_srgb,var(--warning)_8%,transparent)]' : ''}"
									data-testid="vendor-1099-row-{v.vendorId}"
								>
									<td class="py-2 pr-4">
										<a
											href="/vendors/{v.vendorId}"
											class="font-medium text-primary hover:underline"
											data-testid="vendor-1099-name-{v.vendorId}"
										>
											{v.vendorName}
										</a>
									</td>
									<td class="py-2 pr-6 text-right tabular-nums">{formatAccountingCurrency(v.totalPaid)}</td>
									<td class="py-2 pr-4">
										{#if v.w9OnFile}
											<span
												class="m3-tone-chip m3-tone--success inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium"
												data-testid="vendor-1099-w9-{v.vendorId}"
											>
												On file
											</span>
										{:else if v.needsW9}
											<!-- Only 1099-eligible vendors without a W-9 are "Missing" — this is exactly what the
											     header count tallies (needsW9 = is1099Eligible && !w9OnFile), so the badge count and
											     the headline agree. A vendor that isn't 1099-eligible isn't flagged as missing. -->
											<span
												class="m3-tone-chip m3-tone--warning inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium"
												data-testid="vendor-1099-w9-{v.vendorId}"
											>
												Missing
											</span>
										{:else}
											<span
												class="inline-flex items-center text-xs text-muted-foreground"
												data-testid="vendor-1099-w9-{v.vendorId}"
											>
												Not required
											</span>
										{/if}
									</td>
									<td class="py-2 text-right">
										{#if v.needsW9}
											<Button
												variant="outline"
												size="sm"
												onclick={() =>
												requestW9Mutation.mutate({ vendorId: v.vendorId })}
												disabled={requestW9Mutation.isPending}
												data-testid="vendor-1099-request-w9-{v.vendorId}"
											>
												<MessageSquare class="h-3.5 w-3.5" />
												Text W-9 request
											</Button>
										{:else}
											<span class="text-xs text-muted-foreground">—</span>
										{/if}
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
		</Card.Content>
	</Card.Root>

	{#if scheduleEQuery.isLoading}
		<LoadingState label="Loading tax summary" variant="page" testid="tax-loading" />
	{:else if scheduleEQuery.isError}
		<div class="flex flex-wrap items-center justify-center gap-3 rounded-lg border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="tax-error">
			<p class="text-sm text-destructive">Could not load tax data.</p>
			<Button size="sm" variant="outline" onclick={() => scheduleEQuery.refetch()}>Retry tax summary</Button>
		</div>
	{:else if !report || (report.properties.length === 0 && !report.unallocatedActivity.requiresAllocation)}
		<p class="py-12 text-center text-sm text-muted-foreground" data-testid="tax-empty">
			No rental income or expense data for {selectedYear}.
		</p>
	{:else}
		{#if report.unallocatedActivity.requiresAllocation}
			<div class="mb-6 rounded-lg border border-amber-300 bg-amber-50 p-4 text-sm text-amber-950 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-100" data-testid="tax-unallocated-warning">
				<p class="font-semibold">Tax activity needs a property before filing</p>
				<p class="mt-1">{report.unallocatedActivity.incomeEntryCount} income entries ({formatAccountingCurrency(report.unallocatedActivity.rentalIncome)}) and {report.unallocatedActivity.expenseCount} expenses ({formatAccountingCurrency(report.unallocatedActivity.totalExpenses)}) are not assigned to a property, so they are excluded from the Schedule E property lines below.</p>
				<p class="mt-2 font-medium">Reconciled activity: {formatAccountingCurrency(report.reconciledTotalRentalIncome)} income − {formatAccountingCurrency(report.reconciledTotalExpenses)} expenses = {formatAccountingCurrency(report.reconciledNetIncome)} net.</p>
			</div>
		{/if}
		<!-- Grand-total summary cards -->
		<div class="mb-6 grid gap-4 sm:grid-cols-3" data-testid="tax-summary-cards">
			<Card.Root class="gap-0 py-0" data-testid="tax-total-income">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Total rental income</p>
					<p class="text-2xl font-bold text-[var(--success)]">{formatAccountingCurrency(report.totalRentalIncome)}</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="gap-0 py-0" data-testid="tax-total-expenses">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Total expenses</p>
					<p class="text-2xl font-bold text-destructive">{formatAccountingCurrency(report.totalExpenses)}</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="gap-0 py-0" data-testid="tax-net-income">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Net income</p>
					<p
						class="text-2xl font-bold {report.netIncome >= 0
							? 'text-[var(--success)]'
							: 'text-destructive'}"
					>
						{formatAccountingCurrency(report.netIncome)}
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
								<p class="text-lg font-semibold text-[var(--success)]">{formatAccountingCurrency(property.rentalIncome)}</p>
							</div>
							<div>
								<p class="text-xs text-muted-foreground">Total expenses</p>
								<p class="text-lg font-semibold text-destructive">{formatAccountingCurrency(property.totalExpenses)}</p>
							</div>
							<div>
								<p class="text-xs text-muted-foreground">Net income</p>
								<p
									class="text-lg font-semibold {property.netIncome >= 0
										? 'text-[var(--success)]'
										: 'text-destructive'}"
								>
									{formatAccountingCurrency(property.netIncome)}
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
													<td class="py-1.5 text-foreground">{formatExpenseCategory(item.category)}</td>
													<td class="py-1.5 text-right tabular-nums">{formatAccountingCurrency(item.amount)}</td>
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
