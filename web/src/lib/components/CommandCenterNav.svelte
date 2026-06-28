<!--
  Command Center nav entry (pinned, directly below "Scan / Edit"). Surfaces the per-unit
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
	import MaterialSymbol from '$lib/components/m3/MaterialSymbol.svelte';
	import M3NavGroup from '$lib/components/m3/NavGroup.svelte';
	import M3NavItem from '$lib/components/m3/NavItem.svelte';
	import { Search } from '@lucide/svelte';

	let {
		collapsed = false,
		open = false,
		onOpenChange,
		onNavigate
	}: {
		collapsed?: boolean;
		open?: boolean;
		onOpenChange?: (open: boolean) => void;
		onNavigate?: () => void;
	} = $props();

	const portfolioId = $derived(getCurrentPortfolioId());

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
	const active = $derived(page.url.pathname.startsWith('/units/'));

	function setOpen(next: boolean) {
		onOpenChange?.(next);
	}

	function go() {
		// Collapse the dropdown when a unit (or "browse all") is chosen, then run the shell's nav
		// handler (which closes the mobile sidebar). Without resetting `open` it stays expanded after
		// navigating to the unit.
		setOpen(false);
		onNavigate?.();
	}
</script>

{#if collapsed}
	<M3NavItem
		href="/units"
		label="Command Center"
		active={active}
		collapsed
		tone={active ? 'primary' : 'neutral'}
		variant="surface"
		class="mb-1"
		onclick={go}
		data-testid="nav-command-center"
	>
		{#snippet icon()}
			<MaterialSymbol name="monitor_heart" size={20} data-testid="nav-command-center-icon" />
		{/snippet}
	</M3NavItem>
{:else}
	<div data-testid="command-center-nav">
		<M3NavGroup
			label="Command Center"
			active={active}
			expanded={open}
			onclick={() => setOpen(!open)}
			data-testid="nav-command-center"
		>
			{#snippet icon()}
				<MaterialSymbol name="monitor_heart" size={20} data-testid="nav-command-center-icon" />
			{/snippet}
		</M3NavGroup>

		{#if open}
			<div class="m3-motion-reveal-list mt-0.5 space-y-0.5 pl-3">
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
							<M3NavItem
								href="/units/{u.id}"
								onclick={go}
								label="{u.propertyName} · Unit {u.unitNumber}"
								active={u.id === activeUnitId}
								tone={u.id === activeUnitId ? 'primary' : 'neutral'}
								class="min-h-9 py-1.5"
								data-testid="command-center-unit-{u.id}"
							>
								{#snippet icon()}
									<MaterialSymbol name="home" size={18} data-testid="command-center-unit-{u.id}-icon" />
								{/snippet}
							</M3NavItem>
						{/each}
					{/if}
				</div>

				<M3NavItem
					href="/units"
					onclick={go}
					label="Browse all units"
					active={page.url.pathname === '/units'}
					tone={page.url.pathname === '/units' ? 'primary' : 'neutral'}
					class="min-h-9 py-1.5"
					data-testid="command-center-all"
				>
					{#snippet icon()}
						<MaterialSymbol name="home" size={18} data-testid="command-center-all-icon" />
					{/snippet}
				</M3NavItem>
			</div>
		{/if}
	</div>
{/if}
