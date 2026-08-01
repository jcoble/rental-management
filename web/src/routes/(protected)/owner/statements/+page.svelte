<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { ownerPortal } from '$lib/api/endpoints/owner-portal';
	import * as Card from '$lib/components/ui/card';
	import * as Select from '$lib/components/ui/select';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { Button } from '$lib/components/ui/button';
	import { formatDate } from '$lib/utils/date';
	import {
		currentOwnerStatementYear,
		ownerStatementYearOptions,
		watchOwnerStatementYear
	} from '$lib/accounting/owner-statement-years';
	import { onMount } from 'svelte';

	const pageSize = 20;
	let years = $state(ownerStatementYearOptions());
	let selectedYear = $state(String(currentOwnerStatementYear()));
	let selectedOwnerId = $state<number | null>(null);
	let statementSkip = $state(0);
	let distributionSkip = $state(0);
	let lastYear = $state(currentOwnerStatementYear().toString());

	function applyBusinessYear(businessYear: number) {
		years = ownerStatementYearOptions(businessYear);
		selectedYear = String(businessYear);
	}

	onMount(() => watchOwnerStatementYear(applyBusinessYear));

	$effect(() => {
		if (selectedYear !== lastYear) {
			lastYear = selectedYear;
			statementSkip = 0;
			distributionSkip = 0;
		}
	});

	const summariesQuery = createQuery(() => ({
		queryKey: ['owner-portal', 'statements', selectedYear, statementSkip],
		queryFn: () => ownerPortal.statementsPage(Number(selectedYear), { skip: statementSkip, take: pageSize, sort: 'name' })
	}));
	const summaries = $derived(summariesQuery.data?.items ?? []);
	$effect(() => {
		if (summaries.length > 0 && !summaries.some((owner) => owner.ownerId === selectedOwnerId)) selectedOwnerId = summaries[0].ownerId;
	});
	const statementQuery = createQuery(() => ({
		queryKey: ['owner-portal', 'statement', selectedOwnerId, selectedYear],
		queryFn: () => ownerPortal.statement(selectedOwnerId!, Number(selectedYear)),
		enabled: selectedOwnerId !== null
	}));
	const distributionsQuery = createQuery(() => ({
		queryKey: ['owner-portal', 'distributions', selectedYear, distributionSkip],
		queryFn: () => ownerPortal.distributionsPage({ from: `${selectedYear}-01-01`, to: `${selectedYear}-12-31`, sort: '-date', skip: distributionSkip, take: pageSize })
	}));

	function money(value: number) {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value || 0);
	}
</script>

<svelte:head><title>Owner statements | Rental Command</title></svelte:head>

<section class="mx-auto max-w-6xl space-y-6 px-6 py-8" data-testid="owner-statements-page">
	<header class="flex flex-wrap items-start justify-between gap-4" data-testid="owner-statements-header">
		<div class="space-y-2"><p class="text-sm font-medium text-primary">Owner experience</p><h1 class="text-3xl font-semibold tracking-tight">Statements & documents</h1><p class="max-w-2xl text-muted-foreground">Yearly income, paid expenses, management fees, and distributions for your properties.</p></div>
		<Select.Root type="single" bind:value={selectedYear}><Select.Trigger class="w-28" data-testid="owner-statements-year">{selectedYear}</Select.Trigger><Select.Content>{#each years as year}<Select.Item value={String(year)} label={String(year)}>{year}</Select.Item>{/each}</Select.Content></Select.Root>
	</header>
	{#if summariesQuery.isLoading}
		<LoadingState label="Loading owner statements" variant="page" testid="owner-statements-loading" />
	{:else if summariesQuery.isError}
		<div class="rounded-xl border border-destructive/40 px-6 py-10 text-center" data-testid="owner-statements-error"><p class="text-sm font-medium text-destructive">Could not load statements.</p><Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => summariesQuery.refetch()}>Try again</Button></div>
	{:else if summaries.length === 0}
		<p class="py-12 text-center text-sm text-muted-foreground" data-testid="owner-statements-empty">No statement data is available for {selectedYear}.</p>
	{:else}
		<div class="grid gap-6 lg:grid-cols-[260px_1fr]">
			<div class="space-y-2" data-testid="owner-statement-owner-list">{#each summaries as owner (owner.ownerId)}<button type="button" class="w-full rounded-lg border px-4 py-3 text-left {selectedOwnerId === owner.ownerId ? 'border-primary bg-primary/5' : 'border-border bg-card'}" onclick={() => (selectedOwnerId = owner.ownerId)} data-testid="owner-statement-owner-{owner.ownerId}"><span class="font-medium">{owner.ownerName}</span><span class="mt-1 block font-mono text-xs text-muted-foreground">Net {money(owner.netToOwner)}</span></button>{/each}{#if (summariesQuery.data?.totalCount ?? 0) > pageSize}<Pagination bind:skip={statementSkip} take={pageSize} count={summaries.length} hasNext={statementSkip + summaries.length < (summariesQuery.data?.totalCount ?? 0)} testid="owner-statements-pagination" />{/if}</div>
			<div>
				{#if statementQuery.isLoading}<LoadingState label="Loading statement detail" testid="owner-statement-loading" />
				{:else if statementQuery.isError || !statementQuery.data}<div class="rounded-xl border border-destructive/40 px-5 py-8 text-center" data-testid="owner-statement-error"><p class="text-sm font-medium text-destructive">Could not load statement detail.</p><Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => statementQuery.refetch()}>Try again</Button></div>
				{:else}{@const report = statementQuery.data}
					<div class="grid gap-4 sm:grid-cols-2 xl:grid-cols-4" data-testid="owner-statement-totals"><Card.Root class="gap-0 py-0"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Income</p><p class="font-mono text-xl font-semibold">{money(report.totalIncome)}</p></Card.Content></Card.Root><Card.Root class="gap-0 py-0"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Paid expenses</p><p class="font-mono text-xl font-semibold">{money(report.totalExpenses)}</p></Card.Content></Card.Root><Card.Root class="gap-0 py-0"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Net to owner</p><p class="font-mono text-xl font-semibold">{money(report.totalNetToOwner)}</p></Card.Content></Card.Root><Card.Root class="gap-0 py-0"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Undistributed</p><p class="font-mono text-xl font-semibold">{money(report.undistributed)}</p></Card.Content></Card.Root></div>
					<Card.Root class="mt-4 gap-0 py-0" data-testid="owner-statement-properties"><Card.Header class="border-b px-4 py-3"><Card.Title class="text-base">Property breakdown</Card.Title></Card.Header><Card.Content class="divide-y p-0">{#each report.properties as property (property.propertyId)}<div class="grid gap-2 px-4 py-3 sm:grid-cols-[1fr_repeat(3,120px)]"><p class="font-medium">{property.propertyName}</p><p class="text-sm"><span class="block text-xs text-muted-foreground">Income</span>{money(property.rentalIncome)}</p><p class="text-sm"><span class="block text-xs text-muted-foreground">Expenses + fee</span>{money(property.expenses + property.managementFee)}</p><p class="text-sm font-medium"><span class="block text-xs text-muted-foreground">Net</span>{money(property.netToOwner)}</p></div>{/each}</Card.Content></Card.Root>
				{/if}
			</div>
		</div>
	{/if}

	<Card.Root class="gap-0 py-0" data-testid="owner-distributions-card"><Card.Header class="border-b px-4 py-3"><Card.Title class="text-base">Recorded distributions in {selectedYear}</Card.Title></Card.Header><Card.Content class="divide-y p-0">{#if distributionsQuery.isLoading}<div class="p-4"><LoadingState label="Loading owner distributions" testid="owner-distributions-loading" /></div>{:else if distributionsQuery.isError}<div class="p-5 text-center"><p class="text-sm font-medium text-destructive">Could not load distributions.</p><Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => distributionsQuery.refetch()}>Try again</Button></div>{:else if (distributionsQuery.data?.items.length ?? 0) === 0}<p class="p-4 text-sm text-muted-foreground">No distributions are recorded for {selectedYear}.</p>{:else}{#each distributionsQuery.data?.items ?? [] as distribution (distribution.id)}<div class="flex flex-wrap items-center justify-between gap-2 px-4 py-3" data-testid="owner-distribution-{distribution.id}"><div><p class="font-medium">{distribution.propertyName ?? distribution.ownerName}</p><p class="text-sm text-muted-foreground">{formatDate(distribution.date)} · {distribution.method}{distribution.memo ? ` · ${distribution.memo}` : ''}</p></div><p class="font-mono font-semibold tabular-nums">{money(distribution.amount)}</p></div>{/each}{#if (distributionsQuery.data?.totalCount ?? 0) > pageSize}<div class="p-4"><Pagination bind:skip={distributionSkip} take={pageSize} count={distributionsQuery.data?.items.length ?? 0} hasNext={distributionSkip + (distributionsQuery.data?.items.length ?? 0) < (distributionsQuery.data?.totalCount ?? 0)} testid="owner-distributions-pagination" /></div>{/if}{/if}</Card.Content></Card.Root>
	<p class="text-xs text-muted-foreground" data-testid="owner-statement-basis-note">For reference only. Statement totals use collected rent allocations and paid expenses for the selected calendar year.</p>
</section>
