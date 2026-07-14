<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { CheckCircle2, MessageSquare } from '@lucide/svelte';
	import { ownerPortal, type OwnerPortalItemPage } from '$lib/api/endpoints/owner-portal';
	import { formatDate } from '$lib/utils/date';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import * as Card from '$lib/components/ui/card';

	const pageSize = 20;
	let { kind }: { kind: 'approvals' | 'messages' } = $props();
	let skip = $state(0);
	const isApprovals = $derived(kind === 'approvals');
	const itemsQuery = createQuery(() => ({
		queryKey: ['owner-portal', kind, skip],
		queryFn: (): Promise<OwnerPortalItemPage> => isApprovals ? ownerPortal.approvalsPage({ sort: '-createdAt', skip, take: pageSize }) : ownerPortal.messagesPage({ sort: '-createdAt', skip, take: pageSize })
	}));
	const items = $derived(itemsQuery.data?.items ?? []);
</script>

<section class="mx-auto max-w-4xl space-y-6 px-6 py-8" data-testid="owner-{kind}-page">
	<header class="space-y-2" data-testid="owner-{kind}-header"><p class="text-sm font-medium text-primary">Owner experience</p><h1 class="text-3xl font-semibold tracking-tight">{isApprovals ? 'Approvals' : 'Messages'}</h1><p class="text-muted-foreground">{isApprovals ? 'Decisions explicitly routed to your owner relationship appear here.' : 'Updates explicitly shared with your owner relationship appear here.'}</p></header>
	{#if itemsQuery.isLoading}<p class="py-12 text-center text-sm text-muted-foreground" data-testid="owner-{kind}-loading">Loading {kind}…</p>
	{:else if itemsQuery.isError}<p class="py-12 text-center text-sm text-destructive" data-testid="owner-{kind}-error">Could not load {kind}.</p>
	{:else if items.length === 0}<div class="rounded-xl border border-dashed border-border px-6 py-12 text-center" data-testid="owner-{kind}-empty">{#if isApprovals}<CheckCircle2 class="mx-auto h-9 w-9 text-muted-foreground" />{:else}<MessageSquare class="mx-auto h-9 w-9 text-muted-foreground" />{/if}<h2 class="mt-3 font-semibold">{isApprovals ? 'No decisions waiting' : 'No owner messages yet'}</h2><p class="mt-1 text-sm text-muted-foreground">{isApprovals ? 'Your property manager has not routed any approval items to you.' : 'Only messages approved for your owner account will be shown.'}</p></div>
	{:else}<div class="space-y-3" data-testid="owner-{kind}-list">{#each items as item (item.id)}<Card.Root class="gap-0 py-0 {item.isRead ? '' : 'border-primary/40'}" data-testid="owner-{kind}-{item.id}"><Card.Content class="p-5"><div class="flex items-start justify-between gap-4"><div><h2 class="font-semibold">{item.title}</h2><p class="mt-2 whitespace-pre-wrap text-sm text-muted-foreground">{item.message}</p></div>{#if !item.isRead}<span class="rounded-full bg-primary/10 px-2 py-1 text-xs font-medium text-primary">New</span>{/if}</div><p class="mt-3 text-xs text-muted-foreground">{formatDate(item.createdAt)}</p></Card.Content></Card.Root>{/each}{#if (itemsQuery.data?.totalCount ?? 0) > pageSize}<Pagination bind:skip take={pageSize} count={items.length} hasNext={skip + items.length < (itemsQuery.data?.totalCount ?? 0)} testid="owner-{kind}-pagination" />{/if}</div>{/if}
</section>
