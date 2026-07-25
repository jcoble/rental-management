<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { Building2, CheckCircle2, FileText, MessageSquare, WalletCards } from '@lucide/svelte';
	import { ownerPortal } from '$lib/api/endpoints/owner-portal';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';

	const overviewQuery = createQuery(() => ({
		queryKey: ['owner-portal', 'overview'],
		queryFn: () => ownerPortal.overview()
	}));
	const overview = $derived(overviewQuery.data);

	function money(value: number) {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value || 0);
	}

	const destinations = [
		{ href: '/owner/properties', label: 'Properties', detail: 'The rentals connected to your ownership relationship.', icon: Building2 },
		{ href: '/owner/statements', label: 'Statements & documents', detail: 'Review yearly performance and recorded distributions.', icon: FileText },
		{ href: '/owner/approvals', label: 'Approvals', detail: 'Decisions your property manager has routed to you.', icon: CheckCircle2 },
		{ href: '/owner/messages', label: 'Messages', detail: 'Updates explicitly shared with your owner account.', icon: MessageSquare }
	];
</script>

<svelte:head><title>Owner overview | Rental Command</title></svelte:head>

<section class="mx-auto max-w-6xl space-y-8 px-6 py-8" data-testid="owner-experience-home">
	<header class="space-y-2" data-testid="owner-overview-header">
		<p class="text-sm font-medium text-primary" data-testid="owner-experience-label">Owner experience</p>
		<h1 class="text-3xl font-semibold tracking-tight" data-testid="owner-overview-title">Your ownership overview</h1>
		<p class="max-w-2xl text-muted-foreground" data-testid="owner-overview-description">
			Only properties and financial information connected to your owner relationship appear here.
		</p>
	</header>

	{#if overviewQuery.isLoading}
		<LoadingState label="Loading your ownership overview" variant="page" testid="owner-overview-loading" />
	{:else if overviewQuery.isError || !overview}
		<div class="rounded-xl border border-destructive/40 px-6 py-10 text-center" data-testid="owner-overview-error">
			<p class="text-sm font-medium text-destructive">We could not load your owner information.</p>
			<Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => overviewQuery.refetch()}>Try again</Button>
		</div>
	{:else}
		<div class="grid gap-4 sm:grid-cols-2 xl:grid-cols-5" data-testid="owner-overview-metrics">
			<Card.Root class="gap-0 py-0" data-testid="owner-overview-properties"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Properties</p><p class="mt-1 text-2xl font-semibold">{overview.propertyCount}</p></Card.Content></Card.Root>
			<Card.Root class="gap-0 py-0" data-testid="owner-overview-units"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Rental units</p><p class="mt-1 text-2xl font-semibold">{overview.unitCount}</p></Card.Content></Card.Root>
			<Card.Root class="gap-0 py-0" data-testid="owner-overview-distributed"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Distributed in {overview.currentYear}</p><p class="mt-1 font-mono text-2xl font-semibold tabular-nums">{money(overview.distributedThisYear)}</p></Card.Content></Card.Root>
			<Card.Root class="gap-0 py-0" data-testid="owner-overview-approvals"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Pending approvals</p><p class="mt-1 text-2xl font-semibold">{overview.pendingApprovalCount}</p></Card.Content></Card.Root>
			<Card.Root class="gap-0 py-0" data-testid="owner-overview-messages"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Unread messages</p><p class="mt-1 text-2xl font-semibold">{overview.unreadMessageCount}</p></Card.Content></Card.Root>
		</div>
	{/if}

	<div class="grid gap-4 md:grid-cols-2" data-testid="owner-overview-destinations">
		{#each destinations as destination (destination.href)}
			<a href={destination.href} class="group rounded-xl border border-border bg-card p-5 transition-colors hover:border-primary/40 hover:bg-accent/50" data-testid="owner-destination-{destination.label.toLowerCase().replaceAll(' ', '-').replaceAll('&', 'and')}">
				<div class="flex items-start gap-4">
					<div class="rounded-lg bg-primary/10 p-2 text-primary"><destination.icon class="h-5 w-5" /></div>
					<div><h2 class="font-semibold group-hover:text-primary">{destination.label}</h2><p class="mt-1 text-sm text-muted-foreground">{destination.detail}</p></div>
				</div>
			</a>
		{/each}
	</div>

	<div class="flex items-start gap-3 rounded-xl border border-border bg-muted/30 p-4" data-testid="owner-overview-scope-note">
		<WalletCards class="mt-0.5 h-5 w-5 shrink-0 text-muted-foreground" />
		<p class="text-sm text-muted-foreground">Team management, workspace settings, banking connections, billing, and tenant records stay in the management experience and are never available here.</p>
	</div>
</section>
