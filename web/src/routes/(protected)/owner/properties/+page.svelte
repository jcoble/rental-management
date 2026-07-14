<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { Building2, MapPin } from '@lucide/svelte';
	import { ownerPortal } from '$lib/api/endpoints/owner-portal';
	import { Input } from '$lib/components/ui/input';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import * as Card from '$lib/components/ui/card';

	const pageSize = 20;
	let search = $state('');
	let skip = $state(0);
	let lastSearch = $state('');
	$effect(() => {
		const nextSearch = search.trim();
		if (nextSearch !== lastSearch) {
			lastSearch = nextSearch;
			skip = 0;
		}
	});
	const propertiesQuery = createQuery(() => ({
		queryKey: ['owner-portal', 'properties', search.trim(), skip],
		queryFn: () => ownerPortal.propertiesPage({ search, sort: 'name', skip, take: pageSize })
	}));
	const properties = $derived(propertiesQuery.data?.items ?? []);

	function propertyType(value: string) {
		return value.replace(/([a-z])([A-Z])/g, '$1 $2');
	}
</script>

<svelte:head><title>Owner properties | Rental Command</title></svelte:head>

<section class="mx-auto max-w-6xl space-y-6 px-6 py-8" data-testid="owner-properties-page">
	<header class="space-y-2" data-testid="owner-properties-header">
		<p class="text-sm font-medium text-primary">Owner experience</p>
		<h1 class="text-3xl font-semibold tracking-tight">Properties</h1>
		<p class="text-muted-foreground">Only properties connected to your owner relationship are listed.</p>
	</header>
	<div class="max-w-md" data-testid="owner-properties-search-wrap">
		<label for="owner-property-search" class="mb-1.5 block text-sm font-medium">Search properties</label>
		<Input id="owner-property-search" bind:value={search} placeholder="Name, address, or city" data-testid="owner-properties-search" />
	</div>
	{#if propertiesQuery.isLoading}
		<p class="py-12 text-center text-sm text-muted-foreground" data-testid="owner-properties-loading">Loading properties…</p>
	{:else if propertiesQuery.isError}
		<p class="py-12 text-center text-sm text-destructive" data-testid="owner-properties-error">Could not load your properties.</p>
	{:else if properties.length === 0}
		<p class="py-12 text-center text-sm text-muted-foreground" data-testid="owner-properties-empty">{search.trim() ? 'No properties match your search.' : 'No properties are connected to this owner account.'}</p>
	{:else}
		<div class="grid gap-4 md:grid-cols-2 xl:grid-cols-3" data-testid="owner-properties-grid">
			{#each properties as property (property.id)}
				<Card.Root class="gap-0 py-0" data-testid="owner-property-{property.id}">
					<Card.Content class="p-5">
						<div class="flex items-start gap-3"><div class="rounded-lg bg-primary/10 p-2 text-primary"><Building2 class="h-5 w-5" /></div><div class="min-w-0"><h2 class="truncate font-semibold">{property.name}</h2><p class="text-sm text-muted-foreground">{propertyType(property.propertyType)} · {property.unitCount} {property.unitCount === 1 ? 'unit' : 'units'}</p></div></div>
						<div class="mt-4 flex items-start gap-2 text-sm text-muted-foreground"><MapPin class="mt-0.5 h-4 w-4 shrink-0" /><p>{property.addressLine1}{property.addressLine2 ? `, ${property.addressLine2}` : ''}<br />{property.city}, {property.state} {property.postalCode}</p></div>
					</Card.Content>
				</Card.Root>
			{/each}
		</div>
		{#if (propertiesQuery.data?.totalCount ?? 0) > pageSize}<Pagination bind:skip take={pageSize} count={properties.length} hasNext={skip + properties.length < (propertiesQuery.data?.totalCount ?? 0)} testid="owner-properties-pagination" />{/if}
		<p class="text-sm text-muted-foreground" data-testid="owner-properties-count">Showing {skip + 1}–{skip + properties.length} of {propertiesQuery.data?.totalCount ?? properties.length} properties.</p>
	{/if}
</section>
