<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { page } from '$app/state';
	import { units } from '$lib/api/endpoints/units';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { Boxes, ChevronDown, ChevronRight, Search } from '@lucide/svelte';

	let {
		collapsed = false,
		onNavigate
	}: {
		collapsed?: boolean;
		onNavigate?: () => void;
	} = $props();

	const portfolioId = $derived(getCurrentPortfolioId());
	let open = $state(false);
	let search = $state('');
	const debouncedSearch = debounced(() => search, 250);

	const unitsQuery = createQuery(() => ({
		queryKey: ['command-center-units', portfolioId, debouncedSearch.value],
		queryFn: () =>
			units.listWithHealthPage({
				search: debouncedSearch.value,
				sort: 'propertyName',
				skip: 0,
				take: 20
			}),
		staleTime: 5 * 60 * 1000,
		enabled: portfolioId > 0 && open && !collapsed
	}));

	const matchedUnits = $derived(unitsQuery.data?.items ?? []);
	const totalMatches = $derived(unitsQuery.data?.totalCount ?? 0);
	const activeUnitId = $derived(
		page.url.pathname.startsWith('/units/') ? Number(page.url.pathname.split('/')[2]) : null
	);

	function closeAndNavigate() {
		open = false;
		search = '';
		onNavigate?.();
	}
</script>

{#if collapsed}
	<a
		href="/units"
		onclick={closeAndNavigate}
		class="m3-nav-link m3-state-layer mb-1 flex items-center justify-center rounded-[var(--m3-shape-full)] px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-sidebar-accent hover:text-sidebar-foreground"
		aria-label="Command Center"
		data-m3-tooltip="Command Center"
		data-testid="nav-command-center"
	>
		<Boxes class="h-4 w-4" />
	</a>
{:else}
	<div class="mb-1" data-testid="command-center-nav">
		<button
			type="button"
			onclick={() => (open = !open)}
			class="m3-nav-link m3-state-layer flex w-full items-center gap-2 rounded-[var(--m3-shape-full)] px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-sidebar-accent hover:text-sidebar-foreground"
			aria-expanded={open}
			aria-controls="command-center-rental-picker"
			data-testid="nav-command-center"
		>
			<Boxes class="h-4 w-4" />
			<span class="flex-1 truncate text-left">Command Center</span>
			{#if open}
				<ChevronDown class="h-4 w-4" />
			{:else}
				<ChevronRight class="h-4 w-4" />
			{/if}
		</button>

		{#if open}
			<div
				id="command-center-rental-picker"
				class="mb-1 ml-2 mt-1 space-y-1 border-l border-sidebar-border pl-2"
			>
				<div class="relative">
					<Search class="pointer-events-none absolute left-2 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
					<input
						bind:value={search}
						placeholder="Search rentals…"
						aria-label="Search Command Center rentals"
						class="h-8 w-full rounded-md border border-sidebar-border bg-background/60 py-1 pl-8 pr-2 text-xs text-foreground placeholder:text-muted-foreground focus:border-primary focus:outline-none"
						data-testid="command-center-search"
					/>
				</div>

				<div class="max-h-72 space-y-0.5 overflow-y-auto pr-1" aria-live="polite">
					{#if unitsQuery.isLoading}
						<p class="px-2 py-2 text-xs text-muted-foreground">Loading rentals…</p>
					{:else if unitsQuery.isError}
						<div class="px-2 py-2 text-xs">
							<p class="text-destructive">Could not load rentals.</p>
							<button
								type="button"
								class="mt-1 font-medium text-primary hover:underline"
								onclick={() => unitsQuery.refetch()}
							>
								Try again
							</button>
						</div>
					{:else if totalMatches === 0 && search.trim()}
						<p class="px-2 py-2 text-xs text-muted-foreground">No rentals match that search.</p>
					{:else if totalMatches === 0}
						<p class="px-2 py-2 text-xs text-muted-foreground">No rentals yet.</p>
					{:else}
						{#each matchedUnits as unit (unit.id)}
							<a
								href="/units/{unit.id}"
								onclick={closeAndNavigate}
								class="m3-state-layer block rounded-[var(--m3-shape-full)] px-2 py-2 text-xs transition-colors
									{unit.id === activeUnitId
									? 'bg-sidebar-accent font-medium text-sidebar-accent-foreground'
									: 'text-muted-foreground hover:bg-sidebar-accent hover:text-sidebar-foreground'}"
								data-testid="command-center-unit-{unit.id}"
							>
								<span class="block truncate font-medium text-foreground">{unit.propertyName}</span>
								<span class="block truncate opacity-75">Unit {unit.unitNumber}</span>
							</a>
						{/each}
						{#if totalMatches > matchedUnits.length}
							<p class="px-2 py-1 text-[11px] text-muted-foreground">
								Showing {matchedUnits.length} of {totalMatches}. Search to narrow the list.
							</p>
						{/if}
					{/if}
				</div>

				<a
					href="/units"
					onclick={closeAndNavigate}
					class="block rounded-[var(--m3-shape-full)] px-2 py-2 text-xs font-medium text-primary transition-colors hover:bg-sidebar-accent"
					data-testid="command-center-all"
				>
					Browse all rentals
				</a>
			</div>
		{/if}
	</div>
{/if}
