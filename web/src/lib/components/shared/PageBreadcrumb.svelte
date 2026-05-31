<script lang="ts">
	import { HomeIcon, ChevronRightIcon } from '@lucide/svelte';

	let {
		crumbs
	}: {
		/**
		 * Breadcrumb segments. All but the last should have `href`; the last is
		 * rendered as plain text (current page).
		 */
		crumbs: { label: string; href?: string }[];
	} = $props();
</script>

<nav aria-label="Breadcrumb" class="flex items-center gap-1.5 text-sm" data-testid="page-breadcrumb">
	<a
		href="/"
		class="flex items-center text-muted-foreground transition-colors hover:text-foreground"
		aria-label="Home"
	>
		<HomeIcon class="h-4 w-4 shrink-0" />
	</a>
	{#each crumbs as crumb, i}
		<ChevronRightIcon class="h-3.5 w-3.5 shrink-0 text-muted-foreground/50" aria-hidden="true" />
		{#if crumb.href && i < crumbs.length - 1}
			<a
				href={crumb.href}
				class="text-muted-foreground transition-colors hover:text-foreground"
				data-testid="breadcrumb-link-{i}"
			>
				{crumb.label}
			</a>
		{:else}
			<span
				class="font-medium text-foreground"
				aria-current="page"
				data-testid="breadcrumb-current"
			>
				{crumb.label}
			</span>
		{/if}
	{/each}
</nav>
