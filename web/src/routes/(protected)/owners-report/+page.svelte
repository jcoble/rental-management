<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { accounting, downloadOwnerStatementCsv } from '$lib/api/endpoints/accounting';
	import { owners as ownersApi } from '$lib/api/endpoints/owners';
	import {
		ownerDistributions,
		type CreateOwnerDistributionRequest,
		type DistributionMethod,
		type OwnerDistribution
	} from '$lib/api/endpoints/owner-distributions';
	import type { OwnerStatementSummary, OwnerStatementReport } from '$lib/types';
	import { hasCapability } from '$lib/stores/auth.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import * as Card from '$lib/components/ui/card';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { FileBarChart, Mail, Plus, Trash2 } from '@lucide/svelte';

	const CURRENT_YEAR = new Date().getFullYear();
	const YEAR_OPTIONS = Array.from({ length: 5 }, (_, i) => CURRENT_YEAR - i);
	const DISTRIBUTION_METHODS: { value: DistributionMethod; label: string }[] = [
		{ value: 'Ach', label: 'ACH' },
		{ value: 'Check', label: 'Check' },
		{ value: 'Wire', label: 'Wire' },
		{ value: 'Cash', label: 'Cash' },
		{ value: 'Other', label: 'Other' }
	];

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	let selectedYear = $state(String(CURRENT_YEAR));
	let selectedOwnerId = $state<number | null>(null);
	let downloading = $state(false);
	let distributionFormContext = $state('');
	let distributionForm = $state(makeDistributionForm(CURRENT_YEAR));

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
	const distributionQuery = createQuery(() => ({
		queryKey: ['owner-distributions', portfolioId, selectedOwnerId, selectedYear],
		queryFn: () =>
			ownerDistributions.list({
				ownerEntityId: selectedOwnerId!,
				year: Number(selectedYear),
				sort: '-date',
				take: 200
			}),
		enabled: !!portfolioId && selectedOwnerId !== null
	}));
	const distributions = $derived((distributionQuery.data as OwnerDistribution[] | undefined) ?? []);
	const canCreateDistribution = $derived(hasCapability('money.disbursements.manage'));
	const canDeleteDistribution = $derived(
		canCreateDistribution && hasCapability('money.reconciliation.destructive')
	);

	function money(value: number) {
		return new Intl.NumberFormat('en-US', {
			style: 'currency',
			currency: 'USD',
			minimumFractionDigits: 2,
			maximumFractionDigits: 2
		}).format(value || 0);
	}

	function localDateString(date: Date) {
		const year = date.getFullYear();
		const month = String(date.getMonth() + 1).padStart(2, '0');
		const day = String(date.getDate()).padStart(2, '0');
		return `${year}-${month}-${day}`;
	}

	function defaultDistributionDate(year: number) {
		const today = new Date();
		if (today.getFullYear() === year) return localDateString(today);
		return `${year}-12-31`;
	}

	function makeDistributionForm(year: number) {
		return {
			date: defaultDistributionDate(year),
			amount: '',
			method: 'Ach' as DistributionMethod,
			propertyId: 'none',
			memo: ''
		};
	}

	function formatDate(value: string) {
		return new Intl.DateTimeFormat('en-US', {
			month: 'short',
			day: 'numeric',
			year: 'numeric',
			timeZone: 'UTC'
		}).format(new Date(value));
	}

	function methodLabel(value: DistributionMethod) {
		return DISTRIBUTION_METHODS.find((m) => m.value === value)?.label ?? value;
	}

	function parseAmount(value: string) {
		const parsed = Number(value.replace(/[$,]/g, '').trim());
		return Number.isFinite(parsed) ? parsed : NaN;
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

	$effect(() => {
		const key = `${selectedOwnerId ?? 'none'}:${selectedYear}`;
		if (key !== distributionFormContext) {
			distributionFormContext = key;
			distributionForm = makeDistributionForm(Number(selectedYear));
		}
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

	function invalidateDistributionData() {
		queryClient.invalidateQueries({ queryKey: ['owner-distributions'] });
		queryClient.invalidateQueries({ queryKey: ['owner-statements'] });
		queryClient.invalidateQueries({ queryKey: ['owner-statement'] });
		queryClient.invalidateQueries({ queryKey: ['report'] });
		queryClient.invalidateQueries({ queryKey: ['dashboard'] });
	}

	const createDistributionMutation = createMutation(() => ({
		mutationFn: (body: CreateOwnerDistributionRequest) => ownerDistributions.create(body),
		onSuccess: () => {
			showSuccess('Owner distribution recorded.');
			distributionForm = {
				...makeDistributionForm(Number(selectedYear)),
				date: distributionForm.date
			};
			invalidateDistributionData();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteDistributionMutation = createMutation(() => ({
		mutationFn: (id: number) => ownerDistributions.delete(id),
		onSuccess: () => {
			showSuccess('Owner distribution deleted.');
			invalidateDistributionData();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function handleDistributionSubmit(event: SubmitEvent) {
		event.preventDefault();
		if (!canCreateDistribution || selectedOwnerId === null) return;

		const amount = parseAmount(distributionForm.amount);
		if (!Number.isFinite(amount) || amount <= 0) {
			showError('Enter a distribution amount greater than zero.');
			return;
		}
		if (!distributionForm.date) {
			showError('Choose a distribution date.');
			return;
		}

		const propertyId =
			distributionForm.propertyId === 'none' ? undefined : Number(distributionForm.propertyId);
		const body: CreateOwnerDistributionRequest = {
			ownerEntityId: selectedOwnerId,
			propertyId,
			date: `${distributionForm.date}T00:00:00.000Z`,
			amount,
			method: distributionForm.method,
			memo: distributionForm.memo.trim() || undefined
		};
		createDistributionMutation.mutate(body);
	}
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
			<p class="mt-1 max-w-xl text-xs text-muted-foreground" data-testid="owners-report-basis-note">
				Cash basis: only <span class="font-medium">paid</span> expenses count, by the date paid.
				The <a href="/tax" class="underline underline-offset-2">Tax (Schedule E)</a> page uses
				accrual basis (all incurred expenses), so the same property can show a different expense
				total there.
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
		<LoadingState label="Loading owner reports" variant="page" testid="owners-report-loading" />
	{:else if ownersQuery.isError}
		<div class="flex flex-wrap items-center justify-center gap-3 rounded-lg border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="owners-report-error">
			<p class="text-sm text-destructive">Could not load owner data.</p>
			<Button size="sm" variant="outline" onclick={() => ownersQuery.refetch()}>Retry owner reports</Button>
		</div>
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
						<p class="mt-0.5 font-mono text-sm {owner.netToOwner >= 0 ? 'text-[var(--success)]' : 'text-destructive'}">
							Net: {money(owner.netToOwner)}
						</p>
						<p class="mt-0.5 font-mono text-xs {owner.undistributed >= 0 ? 'text-muted-foreground' : 'text-destructive'}">
							Undistributed: {money(owner.undistributed)}
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
					<LoadingState label="Loading owner statement" testid="owners-report-detail-loading" />
				{:else if reportQuery.isError}
					<div class="flex flex-wrap items-center justify-center gap-3 rounded-lg border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="owners-report-detail-error">
						<p class="text-sm text-destructive">Could not load this owner statement.</p>
						<Button size="sm" variant="outline" onclick={() => reportQuery.refetch()}>Retry statement</Button>
					</div>
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
					<div class="mb-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-5" data-testid="owners-report-summary-cards">
						<Card.Root class="gap-0 py-0" data-testid="owners-report-total-income">
							<Card.Content class="p-4">
								<p class="text-xs text-muted-foreground">Total income</p>
								<p class="font-mono tabular-nums text-2xl font-bold text-[var(--success)]">{money(report.totalIncome)}</p>
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
								<p class="font-mono tabular-nums text-2xl font-bold {report.totalNetToOwner >= 0 ? 'text-[var(--success)]' : 'text-destructive'}">
									{money(report.totalNetToOwner)}
								</p>
							</Card.Content>
						</Card.Root>
						<Card.Root class="gap-0 py-0" data-testid="owners-report-undistributed">
							<Card.Content class="p-4">
								<p class="text-xs text-muted-foreground">Undistributed</p>
								<p class="font-mono tabular-nums text-2xl font-bold {report.undistributed >= 0 ? 'text-[var(--success)]' : 'text-destructive'}">
									{money(report.undistributed)}
								</p>
								<p class="mt-1 font-mono text-xs text-muted-foreground">Paid: {money(report.totalDistributed)}</p>
							</Card.Content>
						</Card.Root>
					</div>

						<div class="mb-6 grid gap-4 xl:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)]">
							{#if canCreateDistribution}
								<Card.Root class="gap-0 py-0" data-testid="owner-distribution-form-card">
									<Card.Header class="border-b border-border px-4 py-3">
										<Card.Title class="text-base font-semibold">Record distribution</Card.Title>
									</Card.Header>
									<Card.Content class="p-4">
										<form class="grid gap-3 sm:grid-cols-2" onsubmit={handleDistributionSubmit}>
									<div>
										<label for="owner-distribution-date" class="mb-1 block text-xs font-medium text-muted-foreground">Date</label>
										<Input
											id="owner-distribution-date"
											type="date"
											bind:value={distributionForm.date}
											data-testid="owner-distribution-date"
										/>
									</div>
									<div>
										<label for="owner-distribution-amount" class="mb-1 block text-xs font-medium text-muted-foreground">Amount</label>
										<Input
											id="owner-distribution-amount"
											type="text"
											inputmode="decimal"
											mask="currency"
											bind:value={distributionForm.amount}
											placeholder="0.00"
											data-testid="owner-distribution-amount"
										/>
									</div>
									<div>
										<span class="mb-1 block text-xs font-medium text-muted-foreground">Method</span>
										<Select.Root type="single" bind:value={distributionForm.method}>
											<Select.Trigger class="w-full" data-testid="owner-distribution-method">
												{methodLabel(distributionForm.method)}
											</Select.Trigger>
											<Select.Content>
												{#each DISTRIBUTION_METHODS as method}
													<Select.Item value={method.value} label={method.label}>{method.label}</Select.Item>
												{/each}
											</Select.Content>
										</Select.Root>
									</div>
									<div>
										<span class="mb-1 block text-xs font-medium text-muted-foreground">Property</span>
										<Select.Root type="single" bind:value={distributionForm.propertyId}>
											<Select.Trigger class="w-full" data-testid="owner-distribution-property">
												{distributionForm.propertyId === 'none'
													? 'No property'
													: report.properties.find((p) => String(p.propertyId) === distributionForm.propertyId)?.propertyName ?? 'Property'}
											</Select.Trigger>
											<Select.Content>
												<Select.Item value="none" label="No property">No property</Select.Item>
												{#each report.properties as property}
													<Select.Item value={String(property.propertyId)} label={property.propertyName}>{property.propertyName}</Select.Item>
												{/each}
											</Select.Content>
										</Select.Root>
									</div>
									<div class="sm:col-span-2">
										<label for="owner-distribution-memo" class="mb-1 block text-xs font-medium text-muted-foreground">Memo</label>
										<Input
											id="owner-distribution-memo"
											bind:value={distributionForm.memo}
											maxlength={500}
											placeholder="Optional note"
											data-testid="owner-distribution-memo"
										/>
									</div>
									<div class="sm:col-span-2">
										<Button
											type="submit"
											disabled={createDistributionMutation.isPending}
											data-testid="owner-distribution-submit"
										>
											<Plus class="h-4 w-4" />
											{createDistributionMutation.isPending ? 'Recording…' : 'Record distribution'}
										</Button>
									</div>
										</form>
									</Card.Content>
								</Card.Root>
							{/if}

						<Card.Root class="gap-0 py-0" data-testid="owner-distribution-list-card">
							<Card.Header class="border-b border-border px-4 py-3">
								<Card.Title class="text-base font-semibold">Distributions</Card.Title>
							</Card.Header>
							<Card.Content class="p-0">
								{#if distributionQuery.isLoading}
									<LoadingState label="Loading owner distributions" variant="spinner" testid="owner-distributions-loading" />
								{:else if distributionQuery.isError}
									<div class="flex flex-wrap items-center gap-3 p-4" role="alert" data-testid="owner-distributions-error">
										<p class="text-sm text-destructive">Could not load distributions.</p>
										<Button size="sm" variant="outline" onclick={() => distributionQuery.refetch()}>Retry distributions</Button>
									</div>
								{:else if distributions.length === 0}
									<p class="p-4 text-sm text-muted-foreground" data-testid="owner-distributions-empty">No distributions recorded for {selectedYear}.</p>
								{:else}
									<div class="overflow-x-auto">
										<table class="w-full text-sm">
											<thead>
												<tr class="border-b border-border bg-muted/50">
													<th class="px-4 py-3 text-left font-medium text-muted-foreground">Date</th>
													<th class="px-4 py-3 text-left font-medium text-muted-foreground">Method</th>
													<th class="px-4 py-3 text-left font-medium text-muted-foreground">Property</th>
													<th class="px-4 py-3 text-right font-medium text-muted-foreground">Amount</th>
													<th class="w-12 px-4 py-3"><span class="sr-only">Actions</span></th>
												</tr>
											</thead>
											<tbody>
												{#each distributions as distribution (distribution.id)}
													<tr class="border-b border-border/50 last:border-0 hover:bg-muted/30" data-testid="owner-distribution-row-{distribution.id}">
														<td class="px-4 py-3 whitespace-nowrap">{formatDate(distribution.date)}</td>
														<td class="px-4 py-3 whitespace-nowrap">{methodLabel(distribution.method)}</td>
														<td class="px-4 py-3">{distribution.propertyName ?? '—'}</td>
														<td class="px-4 py-3 text-right font-mono tabular-nums">{money(distribution.amount)}</td>
																<td class="px-4 py-3 text-right">
																	{#if canDeleteDistribution}
																		<Button
																			variant="ghost"
																			size="icon"
																			class="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
																			aria-label={`Delete distribution ${money(distribution.amount)}`}
																			title="Delete distribution"
																			disabled={deleteDistributionMutation.isPending}
																			onclick={() => deleteDistributionMutation.mutate(distribution.id)}
																			data-testid="owner-distribution-delete-{distribution.id}"
																		>
																			<Trash2 class="h-4 w-4" />
																		</Button>
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
													<td class="px-4 py-3 text-right font-mono tabular-nums text-[var(--success)]">{money(prop.rentalIncome)}</td>
													<td class="px-4 py-3 text-right font-mono tabular-nums text-destructive">{money(prop.expenses)}</td>
													<td class="px-4 py-3 text-right font-mono tabular-nums text-destructive">{money(prop.managementFee)}</td>
													<td
														class="px-4 py-3 text-right font-mono tabular-nums font-semibold {prop.netToOwner >= 0
															? 'text-[var(--success)]'
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
