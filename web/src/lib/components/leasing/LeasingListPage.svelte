<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { CalendarDays, ClipboardList, Home, Inbox, Search } from '@lucide/svelte';
	import { leasingWorkspace } from '$lib/api/endpoints/leasing-workspace';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';

	type Kind = 'pipeline' | 'rentals' | 'calendar' | 'inbox';
	let { kind }: { kind: Kind } = $props();
	let search = $state('');
	let submittedSearch = $state('');
	let skip = $state(0);
	const take = 20;

	const meta = $derived.by(() => ({
		pipeline: { title: 'Pipeline', description: 'Applications, listings, agreements, and move-ins that need a next step.', icon: ClipboardList },
		rentals: { title: 'Rentals & listings', description: 'Every assigned rental and its current listing activity.', icon: Home },
		calendar: { title: 'Calendar', description: 'Showings for the rentals assigned to you.', icon: CalendarDays },
		inbox: { title: 'Inbox', description: 'Leasing conversations for your assigned rentals.', icon: Inbox }
	})[kind]);

	const pageQuery = createQuery(() => ({
		queryKey: ['leasing-workspace', kind, skip, submittedSearch],
		queryFn: async () => {
			const params = { skip, take, search: submittedSearch || undefined };
			if (kind === 'pipeline') return await leasingWorkspace.pipelinePage(params);
			if (kind === 'rentals') return await leasingWorkspace.rentalsPage(params);
			if (kind === 'calendar') return await leasingWorkspace.calendarPage({ ...params, sort: 'scheduledStart' });
			return await leasingWorkspace.inboxPage({ ...params, sort: '-lastMessageAt' });
		}
	}));
	const result = $derived(pageQuery.data);

	function submitSearch(event: SubmitEvent) {
		event.preventDefault();
		submittedSearch = search.trim();
		skip = 0;
	}

	function itemHref(item: Record<string, unknown>): string {
		if (kind === 'rentals') return `/leasing/rentals/${item.unitId}`;
		if (kind === 'calendar') return `/leasing/appointments/${item.id}`;
		if (kind === 'inbox') return `/leasing/conversations/${item.id}`;
		if (item.kind === 'Application') return `/leasing/applications/${item.recordId}`;
		if (item.kind === 'Listing') return `/leasing/rentals/${item.unitId}`;
		return `/leasing/move-ins/${item.recordId}`;
	}

	function itemKey(item: unknown): string {
		const record = item as Record<string, unknown>;
		if (kind === 'pipeline' && record.kind != null && record.recordId != null) {
			return `${record.kind}-${record.recordId}`;
		}
		return String(record.unitId ?? record.id ?? `${record.kind}-${record.recordId}`);
	}

	function date(value?: string | null): string {
		return value ? new Intl.DateTimeFormat('en-US', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)) : 'Not scheduled';
	}

	function money(value?: number | null): string {
		return value == null ? 'Rent not set' : new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).format(value);
	}
</script>

<svelte:head><title>{meta.title} | Rental Command</title></svelte:head>

<section class="h-full overflow-y-auto" data-testid="leasing-{kind}-page">
	<div class="mx-auto max-w-6xl space-y-6 px-6 py-8">
		<header class="flex items-start gap-4">
			<div class="rounded-xl bg-primary/10 p-3 text-primary"><meta.icon class="h-6 w-6" /></div>
			<div><p class="text-sm font-medium text-primary">Leasing</p><h1 class="text-3xl font-semibold tracking-tight">{meta.title}</h1><p class="mt-1 text-muted-foreground">{meta.description}</p></div>
		</header>

		<form class="flex max-w-xl gap-2" onsubmit={submitSearch}>
			<div class="relative flex-1"><Search class="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" /><Input bind:value={search} class="pl-9" placeholder={`Search ${meta.title.toLowerCase()}`} aria-label={`Search ${meta.title}`} /></div>
			<Button type="submit" variant="secondary">Search</Button>
		</form>

		{#if pageQuery.isLoading}
			<LoadingState label={`Loading ${meta.title.toLowerCase()}`} variant="page" testid="leasing-{kind}-loading" />
		{:else if pageQuery.isError || !result}
			<div class="rounded-xl border border-destructive/40 p-8 text-center">
				<p class="text-sm font-medium text-destructive">We could not load this leasing work area.</p>
				<Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => pageQuery.refetch()}>Try again</Button>
			</div>
		{:else if result.items.length === 0}
			<div class="rounded-xl border border-dashed p-10 text-center"><meta.icon class="mx-auto h-8 w-8 text-muted-foreground" /><h2 class="mt-3 font-semibold">Nothing needs attention here</h2><p class="mt-1 text-sm text-muted-foreground">Try another search or return later when new leasing work arrives.</p></div>
		{:else}
			<div class="space-y-3" data-testid="leasing-{kind}-items">
				{#each result.items as raw (itemKey(raw))}
					{@const item = raw as Record<string, any>}
					<a href={itemHref(item)} class="block rounded-xl border bg-card p-4 transition-colors hover:border-primary/40 hover:bg-accent/30">
						{#if kind === 'pipeline'}
							<div class="flex items-start justify-between gap-4"><div><p class="text-xs font-medium uppercase tracking-wide text-primary">{item.kind}</p><h2 class="font-semibold">{item.title}</h2><p class="text-sm text-muted-foreground">{[item.propertyName, item.unitNumber && `Unit ${item.unitNumber}`].filter(Boolean).join(' · ') || 'Workspace-wide'}</p></div><span class="rounded-full bg-secondary px-3 py-1 text-xs font-medium">{item.stage}</span></div>
							<p class="mt-3 text-xs text-muted-foreground">Next date: {date(item.nextActionAtUtc)}</p>
						{:else if kind === 'rentals'}
							<div class="flex items-start justify-between gap-4"><div><h2 class="font-semibold">{item.propertyName} · Unit {item.unitNumber}</h2><p class="text-sm text-muted-foreground">{item.address}</p><p class="mt-2 text-sm">{item.listingHeadline || 'No listing started'} · {money(item.askingRent)}</p></div><span class="rounded-full bg-secondary px-3 py-1 text-xs font-medium">{item.listingStatus || 'Not listed'}</span></div>
							{#if item.canViewApplications || item.canViewShowings}
								<p class="mt-3 text-xs text-muted-foreground">
									{#if item.canViewApplications}<span>{item.openApplicationCount} open applications</span>{/if}
									{#if item.canViewApplications && item.canViewShowings}<span aria-hidden="true"> · </span>{/if}
									{#if item.canViewShowings}<span>Next showing {date(item.nextShowingAtUtc)}</span>{/if}
								</p>
							{/if}
						{:else if kind === 'calendar'}
							<div class="flex items-start justify-between gap-4"><div><h2 class="font-semibold">{item.title}</h2><p class="text-sm text-muted-foreground">{item.prospectName || 'Prospect'} · {[item.propertyName, item.unitNumber && `Unit ${item.unitNumber}`].filter(Boolean).join(' · ')}</p></div><span class="rounded-full bg-secondary px-3 py-1 text-xs font-medium">{item.status}</span></div><p class="mt-3 text-sm font-medium">{date(item.scheduledStart)}</p>
						{:else}
							<div class="flex items-start justify-between gap-4"><div><h2 class="font-semibold">{item.tenantName}</h2><p class="text-sm text-muted-foreground">{item.subject}{item.propertyName ? ` · ${item.propertyName}` : ''}</p><p class="mt-2 line-clamp-2 text-sm">{item.lastMessagePreview || 'No message preview'}</p></div>{#if item.unreadCount > 0}<span class="rounded-full bg-primary px-2 py-1 text-xs font-semibold text-primary-foreground">{item.unreadCount}</span>{/if}</div><p class="mt-3 text-xs text-muted-foreground">{date(item.lastMessageAt)}</p>
						{/if}
					</a>
				{/each}
			</div>
			<div class="flex items-center justify-between"><p class="text-sm text-muted-foreground">Showing {result.skip + 1}–{Math.min(result.skip + result.items.length, result.totalCount)} of {result.totalCount}</p><div class="flex gap-2"><Button variant="outline" disabled={result.skip === 0} onclick={() => (skip = Math.max(0, skip - take))}>Previous</Button><Button variant="outline" disabled={result.skip + result.items.length >= result.totalCount} onclick={() => (skip += take)}>Next</Button></div></div>
		{/if}
	</div>
</section>
