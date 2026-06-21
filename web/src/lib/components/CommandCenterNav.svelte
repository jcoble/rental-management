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
	import { Boxes, ChevronDown, ChevronRight, Search } from '@lucide/svelte';

	let { collapsed = false, onNavigate }: { collapsed?: boolean; onNavigate?: () => void } = $props();

	const portfolioId = $derived(getCurrentPortfolioId());

	// One call carries property name + unit number (so labels aren't ambiguous "Unit 1, Unit 1…").
	const unitsQuery = createQuery(() => ({
		queryKey: ['command-center-units', portfolioId],
		queryFn: () => unitsApi.listWithHealth({ take: 500 }),
		enabled: portfolioId > 0
	}));

	const sortedUnits = $derived(
		[...(unitsQuery.data ?? [])].sort(
			(a, b) =>
				a.propertyName.localeCompare(b.propertyName) ||
				a.unitNumber.localeCompare(b.unitNumber, undefined, { numeric: true })
		)
	);

	// Closed by default; the user opens it, picks a unit, and it collapses again on selection.
	let open = $state(false);
	let search = $state('');

	const activeUnitId = $derived(
		page.url.pathname.startsWith('/units/') ? Number(page.params.id) : NaN
	);

	const filtered = $derived.by(() => {
		const q = search.trim().toLowerCase();
		if (!q) return sortedUnits;
		return sortedUnits.filter((u) => `${u.propertyName} ${u.unitNumber}`.toLowerCase().includes(q));
	});

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
				{#if sortedUnits.length > 6}
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
					{:else if sortedUnits.length === 0}
						<p class="px-2 py-1.5 text-xs text-muted-foreground">No units yet.</p>
					{:else if filtered.length === 0}
						<p class="px-2 py-1.5 text-xs text-muted-foreground">No matches.</p>
					{:else}
						{#each filtered as u (u.id)}
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
