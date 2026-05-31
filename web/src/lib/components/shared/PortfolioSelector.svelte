<script lang="ts">
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { getCurrentPortfolioId, setCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import type { Portfolio } from '$lib/types';
	import { ChevronDown, Plus, Building2 } from '@lucide/svelte';
	import CreatePortfolioDialog from './CreatePortfolioDialog.svelte';

	let { collapsed = false }: { collapsed?: boolean } = $props();

	const queryClient = useQueryClient();

	const portfoliosQuery = createQuery(() => ({
		queryKey: ['portfolios'],
		queryFn: () => portfolios.list(),
	}));

	let open = $state(false);
	let showCreateDialog = $state(false);

	let currentPortfolio = $derived(
		portfoliosQuery.data?.find((p: Portfolio) => p.id === getCurrentPortfolioId())
	);

	$effect(() => {
		const list = portfoliosQuery.data;
		if (list && list.length > 0 && !list.find((p: Portfolio) => p.id === getCurrentPortfolioId())) {
			setCurrentPortfolioId(list[0].id);
		}
	});

	function selectPortfolio(id: number) {
		setCurrentPortfolioId(id);
		open = false;
		queryClient.invalidateQueries();
	}

	function handleCreated(portfolio: Portfolio) {
		showCreateDialog = false;
		queryClient.invalidateQueries({ queryKey: ['portfolios'] });
		selectPortfolio(portfolio.id);
	}

	function handleClickOutside(e: MouseEvent) {
		const target = e.target as HTMLElement;
		if (!target.closest('.portfolio-selector')) {
			open = false;
		}
	}
</script>

<svelte:window onclick={handleClickOutside} />

<div class="portfolio-selector relative px-2 pb-2">
	{#if collapsed}
		<button
			onclick={() => (open = !open)}
			class="flex w-full items-center justify-center rounded-md px-3 py-2 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
			title={currentPortfolio?.name || 'Select portfolio'}
		>
			<Building2 class="h-4 w-4 shrink-0" />
		</button>
	{:else}
		<button
			onclick={() => (open = !open)}
			class="flex w-full items-center gap-2 rounded-md border border-border bg-background px-3 py-1.5 text-sm text-foreground transition-colors hover:border-border"
		>
			<Building2 class="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
			<span class="flex-1 truncate text-left">{currentPortfolio?.name || 'Select portfolio'}</span>
			<ChevronDown class="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
		</button>
	{/if}

	{#if open}
		<div class="absolute left-2 right-2 top-full z-50 mt-1 rounded-md border border-border bg-card shadow-lg">
			{#if portfoliosQuery.data}
				<div class="max-h-48 overflow-y-auto py-1">
					{#each portfoliosQuery.data as portfolio}
						<button
							onclick={() => selectPortfolio(portfolio.id)}
							class="flex w-full items-center gap-2 px-3 py-1.5 text-sm transition-colors hover:bg-secondary {portfolio.id === getCurrentPortfolioId() ? 'text-primary' : 'text-foreground'}"
						>
							<span class="truncate">{portfolio.name}</span>
							{#if portfolio.id === getCurrentPortfolioId()}
								<span class="ml-auto text-[10px] text-primary">current</span>
							{/if}
						</button>
					{/each}
				</div>
			{/if}
			<div class="border-t border-border py-1">
				<button
					onclick={() => { open = false; showCreateDialog = true; }}
					class="flex w-full items-center gap-2 px-3 py-1.5 text-sm text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
				>
					<Plus class="h-3.5 w-3.5" />
					New Portfolio
				</button>
			</div>
		</div>
	{/if}
</div>

{#if showCreateDialog}
	<CreatePortfolioDialog
		onCreated={handleCreated}
		onClose={() => (showCreateDialog = false)}
	/>
{/if}
