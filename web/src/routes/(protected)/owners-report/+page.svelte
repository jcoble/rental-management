<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import { accounting, downloadOwnerStatementCsv } from '$lib/api/endpoints/accounting';
	import { owners as ownersApi } from '$lib/api/endpoints/owners';
	import type { OwnerStatementSummary, OwnerStatementReport } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import * as Card from '$lib/components/ui/card';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { FileBarChart, Mail } from '@lucide/svelte';

	const CURRENT_YEAR = new Date().getFullYear();
	const YEAR_OPTIONS = Array.from({ length: 5 }, (_, i) => CURRENT_YEAR - i);

	const portfolioId = $derived(getCurrentPortfolioId());
	let selectedYear = $state(String(CURRENT_YEAR));
	let selectedOwnerId = $state<number | null>(null);
	let downloading = $state(false);

	const ownersQuery = createQuery(() => ({
		queryKey: ['owner-statements', portfolioId, selectedYear],
		queryFn: () => accounting.ownerStatements(Number(selectedYear)),
		enabled: !!portfolioId
	}));

	const owners = $derived((ownersQuery.data as OwnerStatementSummary[] | undefined) ?? []);

	const reportQuery = createQuery(() => ({
		queryKey: ['owner-statement', portfolioId, selectedOwnerId, selectedYear],
		queryFn: () => accounting.ownerStatement(selectedOwnerId!, Number(selectedYear)),
		enabled: !!portfolioId && selectedOwnerId !== null
	}));

	const report = $derived(reportQuery.data as OwnerStatementReport | undefined);

	function money(value: number) {
		return new Intl.NumberFormat('en-US', {
			style: 'currency',
			currency: 'USD',
			maximumFractionDigits: 0
		}).format(value || 0);
	}

	function selectOwner(ownerId: number) {
		selectedOwnerId = ownerId;
	}

	// Reset selected owner when year changes
	$effect(() => {
		// eslint-disable-next-line @typescript-eslint/no-unused-expressions
		selectedYear;
		selectedOwnerId = null;
	});

	async function handleDownload() {
		if (selectedOwnerId === null) return;
		downloading = true;
		try {
			await downloadOwnerStatementCsv(selectedOwnerId, Number(selectedYear));
		} catch {
			showError('Could not download CSV. Please try again.');
		} finally {
			downloading = false;
		}
	}

	const emailStatementMutation = createMutation(() => ({
		mutationFn: ({ ownerId, year }: { ownerId: number; year: number }) =>
			ownersApi.emailStatement(ownerId, year),
		onSuccess: () => showSuccess('Statement emailed.'),
		onError: (err) => showError(apiErrorMessage(err)),
	}));
</script>

<svelte:head>
	<title>Owner Reports - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="owners-report-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-4">
		<div>
			<h1 class="flex items-center gap-2 text-2xl font-bold">
				<FileBarChart class="h-6 w-6 text-primary" />
				Owner Reports
			</h1>
			<p class="mt-1 text-sm text-muted-foreground">
				Owner statement — for reference, not a tax document. Share with property owners to
				summarize income, expenses, and net distributions.
			</p>
		</div>
		<div class="flex items-center gap-2">
			<Select.Root type="single" bind:value={selectedYear}>
				<Select.Trigger class="w-28" data-testid="owners-report-year-select">
					{selectedYear}
				</Select.Trigger>
				<Select.Content>
					{#each YEAR_OPTIONS as year}
						<Select.Item value={String(year)} label={String(year)}>{year}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
		</div>
	</div>

	{#if ownersQuery.isLoading}
		<p class="py-12 text-center text-sm text-muted-foreground" data-testid="owners-report-loading">
			Loading owners…
		</p>
	{:else if ownersQuery.isError}
		<p class="py-12 text-center text-sm text-destructive" data-testid="owners-report-error">
			Could not load owner data. Please try again.
		</p>
	{:else if owners.length === 0}
		<p class="py-12 text-center text-sm text-muted-foreground" data-testid="owners-report-empty">
			No owner data for {selectedYear}.
		</p>
	{:else}
		<div class="grid gap-6 lg:grid-cols-[280px_1fr]">
			<!-- Owner list -->
			<div class="space-y-2" data-testid="owners-report-list">
				{#each owners as owner (owner.ownerId)}
					<button
						type="button"
						onclick={() => selectOwner(owner.ownerId)}
						data-testid="owners-report-owner-{owner.ownerId}"
						class="w-full rounded-lg border border-border bg-card px-4 py-3 text-left transition-colors hover:bg-accent
							{selectedOwnerId === owner.ownerId
							? 'border-primary/60 bg-primary/5 ring-1 ring-primary/30'
							: ''}"
					>
						<p class="font-medium text-foreground">{owner.ownerName}</p>
						<p class="mt-0.5 font-mono text-sm {owner.netToOwner >= 0 ? 'text-green-600' : 'text-destructive'}">
							Net: {money(owner.netToOwner)}
						</p>
					</button>
				{/each}
			</div>

			<!-- Report panel -->
			<div data-testid="owners-report-detail">
				{#if selectedOwnerId === null}
					<p class="py-12 text-center text-sm text-muted-foreground">
						Select an owner to view their statement.
					</p>
				{:else if reportQuery.isLoading}
					<p class="py-12 text-center text-sm text-muted-foreground" data-testid="owners-report-detail-loading">
						Loading statement…
					</p>
				{:else if reportQuery.isError}
					<p class="py-12 text-center text-sm text-destructive" data-testid="owners-report-detail-error">
						Could not load statement. Please try again.
					</p>
				{:else if report}
					<!-- Header + download -->
					<div class="mb-4 flex flex-wrap items-center justify-between gap-3">
						<h2 class="text-lg font-semibold">{report.ownerName} — {report.year}</h2>
						<div class="flex items-center gap-2">
							<Button
								variant="outline"
								onclick={() => selectedOwnerId !== null && emailStatementMutation.mutate({ ownerId: selectedOwnerId, year: Number(selectedYear) })}
								disabled={emailStatementMutation.isPending}
								data-testid="owner-statement-email-{report.ownerId}"
							>
								<Mail class="mr-1.5 h-4 w-4" />
								{emailStatementMutation.isPending ? 'Sending…' : 'Email to owner'}
							</Button>
							<Button
								variant="outline"
								onclick={handleDownload}
								disabled={downloading}
								data-testid="owners-report-download-csv"
							>
								{downloading ? 'Downloading…' : 'Download CSV'}
							</Button>
						</div>
					</div>

					<!-- Grand-total cards -->
					<div class="mb-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-4" data-testid="owners-report-summary-cards">
						<Card.Root class="gap-0 py-0" data-testid="owners-report-total-income">
							<Card.Content class="p-4">
								<p class="text-xs text-muted-foreground">Total income</p>
								<p class="font-mono tabular-nums text-2xl font-bold text-green-600">{money(report.totalIncome)}</p>
							</Card.Content>
						</Card.Root>
						<Card.Root class="gap-0 py-0" data-testid="owners-report-total-expenses">
							<Card.Content class="p-4">
								<p class="text-xs text-muted-foreground">Total expenses</p>
								<p class="font-mono tabular-nums text-2xl font-bold text-destructive">{money(report.totalExpenses)}</p>
							</Card.Content>
						</Card.Root>
						<Card.Root class="gap-0 py-0" data-testid="owners-report-mgmt-fee">
							<Card.Content class="p-4">
								<p class="text-xs text-muted-foreground">Management fee</p>
								<p class="font-mono tabular-nums text-2xl font-bold text-destructive">{money(report.totalManagementFee)}</p>
							</Card.Content>
						</Card.Root>
						<Card.Root class="gap-0 py-0" data-testid="owners-report-net">
							<Card.Content class="p-4">
								<p class="text-xs text-muted-foreground">Net to owner</p>
								<p class="font-mono tabular-nums text-2xl font-bold {report.totalNetToOwner >= 0 ? 'text-green-600' : 'text-destructive'}">
									{money(report.totalNetToOwner)}
								</p>
							</Card.Content>
						</Card.Root>
					</div>

					<!-- Property breakdown table -->
					{#if report.properties.length > 0}
						<Card.Root class="gap-0 py-0" data-testid="owners-report-properties-table">
							<Card.Header class="border-b border-border px-4 py-3">
								<Card.Title class="text-base font-semibold">Property breakdown</Card.Title>
							</Card.Header>
							<Card.Content class="p-0">
								<div class="overflow-x-auto">
									<table class="w-full text-sm">
										<thead>
											<tr class="border-b border-border bg-muted/50">
												<th class="px-4 py-3 text-left font-medium text-muted-foreground">Property</th>
												<th class="px-4 py-3 text-right font-medium text-muted-foreground">Income</th>
												<th class="px-4 py-3 text-right font-medium text-muted-foreground">Expenses</th>
												<th class="px-4 py-3 text-right font-medium text-muted-foreground">Mgmt fee</th>
												<th class="px-4 py-3 text-right font-medium text-muted-foreground">Net</th>
											</tr>
										</thead>
										<tbody>
											{#each report.properties as prop (prop.propertyId)}
												<tr
													class="border-b border-border/50 last:border-0 hover:bg-muted/30"
													data-testid="owners-report-property-row-{prop.propertyId}"
												>
													<td class="px-4 py-3 font-medium text-foreground">{prop.propertyName}</td>
													<td class="px-4 py-3 text-right font-mono tabular-nums text-green-600">{money(prop.rentalIncome)}</td>
													<td class="px-4 py-3 text-right font-mono tabular-nums text-destructive">{money(prop.expenses)}</td>
													<td class="px-4 py-3 text-right font-mono tabular-nums text-destructive">{money(prop.managementFee)}</td>
													<td
														class="px-4 py-3 text-right font-mono tabular-nums font-semibold {prop.netToOwner >= 0
															? 'text-green-600'
															: 'text-destructive'}"
													>
														{money(prop.netToOwner)}
													</td>
												</tr>
											{/each}
										</tbody>
									</table>
								</div>
							</Card.Content>
						</Card.Root>
					{/if}
				{/if}
			</div>
		</div>
	{/if}
</div>
