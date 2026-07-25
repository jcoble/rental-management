<script lang="ts">
	import { page } from '$app/state';
	import MaterialSymbol from '$lib/components/m3/MaterialSymbol.svelte';
	import M3NavItem from '$lib/components/m3/NavItem.svelte';

	let {
		collapsed = false,
		onNavigate
	}: {
		collapsed?: boolean;
		onNavigate?: () => void;
	} = $props();

	function go() {
		onNavigate?.();
	}
</script>

<!-- Legacy import compatibility: Unit navigation now returns to the canonical Rentals entry. -->
<M3NavItem
	href="/units"
	label="Rentals"
	active={page.url.pathname === '/units' || page.url.pathname.startsWith('/units/')}
	{collapsed}
	tone={page.url.pathname === '/units' || page.url.pathname.startsWith('/units/')
		? 'primary'
		: 'neutral'}
	variant="surface"
	onclick={go}
	data-testid="nav-rentals-units"
>
	{#snippet icon()}
		<MaterialSymbol name="home" size={20} data-testid="nav-rentals-units-icon" />
	{/snippet}
</M3NavItem>
