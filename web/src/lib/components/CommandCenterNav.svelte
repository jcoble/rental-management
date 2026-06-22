<!--
  Command Center nav entry (pinned, directly below "Scan / Add"). Surfaces the per-unit
  Command Center (/units/[id]) that was otherwise buried under Rentals → Units. Expands to a
  searchable list of every unit labelled by property + unit number; each opens that unit's
  Command Center. In the collapsed rail it degrades to an icon link to the units list.
-->
<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { units as unitsApi } from '$lib/api/endpoints/units';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { page } from '$app/state';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { Boxes, ChevronDown, ChevronRight, Search } from '@lucide/svelte';

	let { collapsed = false, onNavigate }: { collapsed?: boolean; onNavigate?: () => void } = $props();

	const portfolioId = $derived(getCurrentPortfolioId());

	// Closed by default; the user opens it, picks a unit, and it collapses again on selection.
	let open = $state(false);
	let search = $state('');
	const debouncedSearch = debounced(() => search, 250);

	// One paged query carries property name + unit number (so labels aren't ambiguous "Unit 1, Unit 1…").
	// It runs only while the command-center dropdown is open; search is applied server-side.
	const unitsQuery = createQuery(() => ({
		queryKey: ['command-center-units', portfolioId, debouncedSearch.value],
		queryFn: () => unitsApi.listWithHealthPage({
			search: debouncedSearch.value,
			sort: 'propertyName',
			take: 20
		}),
		enabled: portfolioId > 0 && open && !collapsed
	}));

	const matchedUnits = $derived(unitsQuery.data?.items ?? []);
	const totalMatches = $derived(unitsQuery.data?.totalCount ?? 0);

	const activeUnitId = $derived(
		page.url.pathname.startsWith('/units/') ? Number(page.params.id) : NaN
	);

	function go() {
		// Collapse the dropdown when a unit (or "browse all") is chosen, then run the shell's nav
		// handler (which closes the mobile sidebar). Without resetting `open` it stays expanded after
		// navigating to the unit.
		open = false;
		onNavigate?.();
	}
</script>

{#if collapsed}
	<a
		href="/units"
		onclick={go}
		class="m3-nav-link m3-state-layer flex items-center justify-center rounded-[var(--m3-shape-full)] px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-sidebar-accent hover:text-sidebar-foreground"
		aria-label="Command Center"
		data-m3-tooltip="Command Center"
		data-testid="nav-command-center"
	>
		<Boxes class="h-4 w-4 shrink-0" />
	</a>
{:else}
	<div data-testid="command-center-nav">
		<button
			type="button"
			onclick={() => (open = !open)}
			class="m3-nav-link m3-state-layer flex w-full items-center gap-2 rounded-[var(--m3-shape-full)] px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-sidebar-accent hover:text-sidebar-foreground"
			aria-expanded={open}
			data-testid="nav-command-center"
		>
			<Boxes class="h-4 w-4 shrink-0" />
			<span class="flex-1 truncate text-left">Command Center</span>
			{#if open}
				<ChevronDown class="h-3.5 w-3.5 shrink-0 opacity-60" />
			{:else}
				<ChevronRight class="h-3.5 w-3.5 shrink-0 opacity-60" />
			{/if}
		</button>

		{#if open}
			<div class="mb-1 ml-2 mt-0.5 space-y-0.5 border-l border-sidebar-border pl-2">
				{#if totalMatches > 6 || search.trim().length > 0}
					<div class="relative mb-1">
						<Search
							class="pointer-events-none absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground"
						/>
						<input
							bind:value={search}
							placeholder="Search units…"
							class="w-full rounded-md border border-sidebar-border bg-background/60 py-1 pl-7 pr-2 text-xs text-foreground placeholder:text-muted-foreground focus:border-primary focus:outline-none"
							data-testid="command-center-search"
						/>
					</div>
				{/if}

				<div class="max-h-72 space-y-0.5 overflow-y-auto pr-1">
					{#if unitsQuery.isLoading}
						<p class="px-2 py-1.5 text-xs text-muted-foreground">Loading units…</p>
					{:else if totalMatches === 0 && search.trim().length > 0}
						<p class="px-2 py-1.5 text-xs text-muted-foreground">No matches.</p>
					{:else if totalMatches === 0}
						<p class="px-2 py-1.5 text-xs text-muted-foreground">No units yet.</p>
					{:else}
						{#each matchedUnits as u (u.id)}
							<a
								href="/units/{u.id}"
								onclick={go}
								class="m3-state-layer flex items-center gap-2 rounded-[var(--m3-shape-full)] px-2 py-1.5 text-xs transition-colors
									{u.id === activeUnitId
									? 'bg-sidebar-accent font-medium text-sidebar-accent-foreground'
									: 'text-muted-foreground hover:bg-sidebar-accent hover:text-sidebar-foreground'}"
								data-testid="command-center-unit-{u.id}"
							>
								<span class="min-w-0 flex-1 truncate">
									{u.propertyName}<span class="opacity-60"> · Unit {u.unitNumber}</span>
								</span>
							</a>
						{/each}
					{/if}
				</div>

				<a
					href="/units"
					onclick={go}
					class="block rounded-[var(--m3-shape-full)] px-2 py-1.5 text-xs font-medium text-primary transition-colors hover:bg-sidebar-accent"
					data-testid="command-center-all"
				>
					Browse all units →
				</a>
			</div>
		{/if}
	</div>
{/if}
