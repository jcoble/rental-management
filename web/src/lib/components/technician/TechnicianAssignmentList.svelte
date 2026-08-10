<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { technician, type TechnicianAssignmentParams } from '$lib/api/endpoints/technician';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import RangeDatePicker from '$lib/components/shared/RangeDatePicker.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Select from '$lib/components/ui/select';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { CalendarClock, ChevronRight, Inbox, MapPin, Wrench } from '@lucide/svelte';

	let {
		mode = 'work',
		title,
		description
	}: {
		mode?: 'work' | 'schedule' | 'inbox';
		title: string;
		description: string;
	} = $props();

	const PAGE_SIZE = 20;
	let search = $state('');
	let status = $state('');
	let scheduledFrom = $state('');
	let scheduledTo = $state('');
	let page = $state(1);
	const debouncedSearch = debounced(() => search, 300);

	$effect(() => {
		debouncedSearch.value;
		status;
		scheduledFrom;
		scheduledTo;
		page = 1;
	});

	const query = createQuery(() => {
		const params: TechnicianAssignmentParams = {
			search: debouncedSearch.value || undefined,
			status: status ? status as TechnicianAssignmentParams['status'] : undefined,
			openOnly: mode === 'work',
			scheduledFrom: scheduledFrom ? new Date(`${scheduledFrom}T00:00:00`).toISOString() : undefined,
			scheduledTo: scheduledTo ? new Date(`${scheduledTo}T23:59:59`).toISOString() : undefined,
			skip: (page - 1) * PAGE_SIZE,
			take: PAGE_SIZE
		};
		return {
			queryKey: ['technician', mode, params],
			queryFn: () => mode === 'schedule' ? technician.schedule(params) :
				mode === 'inbox' ? technician.inbox(params) : technician.assignments(params)
		};
	});

	const totalPages = $derived(Math.max(1, Math.ceil((query.data?.totalCount ?? 0) / PAGE_SIZE)));
	const totalCount = $derived(query.data?.totalCount ?? 0);
	const rangeStart = $derived(totalCount === 0 ? 0 : (page - 1) * PAGE_SIZE + 1);
	const rangeEnd = $derived(Math.min(page * PAGE_SIZE, totalCount));
	const EmptyIcon = $derived(mode === 'schedule' ? CalendarClock : mode === 'inbox' ? Inbox : Wrench);

	function when(start?: string | null, end?: string | null): string {
		if (!start) return 'Not scheduled';
		const startText = new Date(start).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' });
		if (!end) return startText;
		const endText = new Date(end).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
		return `${startText} – ${endText}`;
	}
</script>

<svelte:head><title>{title} - Rental Command</title></svelte:head>

<div class="mx-auto w-full max-w-6xl space-y-6 p-4 sm:p-6">
	<PageHeader eyebrow="Maintenance technician" {title} {description} band tone="sky" art={5} />

	<section class="grid gap-3 rounded-2xl border border-border bg-card p-4 md:grid-cols-[minmax(16rem,1fr)_12rem_auto_auto]">
		<SearchInput bind:value={search} placeholder="Search assigned work" testid="technician-search" />
		<Select.Root type="single" bind:value={status}>
			<Select.Trigger class="w-full" aria-label="Filter by status" data-testid="technician-status-filter">
				{status === 'InProgress'
					? 'In progress'
					: status === 'WaitingParts'
						? 'Waiting for parts'
						: status || 'All statuses'}
			</Select.Trigger>
			<Select.Content>
				<Select.Item value="" label="All statuses">All statuses</Select.Item>
				<Select.Item value="New" label="New">New</Select.Item>
				<Select.Item value="Scheduled" label="Scheduled">Scheduled</Select.Item>
				<Select.Item value="InProgress" label="In progress">In progress</Select.Item>
				<Select.Item value="WaitingParts" label="Waiting for parts">Waiting for parts</Select.Item>
				<Select.Item value="Completed" label="Completed">Completed</Select.Item>
			</Select.Content>
		</Select.Root>
		{#if mode === 'schedule'}
			<div class="min-w-0 md:col-span-2">
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="technician-schedule-range">Scheduled between</label>
				<RangeDatePicker id="technician-schedule-range" bind:start={scheduledFrom} bind:end={scheduledTo} testid="technician-schedule-range" placeholder="Choose schedule range" />
			</div>
		{/if}
	</section>

	{#if query.isPending}
		<LoadingState label="Loading assigned work" variant="page" testid="technician-assignment-list-loading" />
	{:else if query.isError}
		<div class="rounded-2xl border border-destructive/40 bg-destructive/5 p-6">
			<p class="text-sm font-medium text-destructive">Assigned work is unavailable right now.</p>
			<Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => query.refetch()}>Try again</Button>
		</div>
	{:else if (query.data?.items.length ?? 0) === 0}
		<div class="rounded-2xl border border-dashed border-border bg-card p-10 text-center">
			<EmptyIcon class="mx-auto mb-3 h-8 w-8 text-muted-foreground" />
			<p class="font-medium">Nothing here right now</p>
			<p class="mt-1 text-sm text-muted-foreground">Only work currently assigned to you appears here.</p>
		</div>
	{:else}
		<div class="space-y-3">
			{#each query.data?.items ?? [] as assignment (assignment.id)}
				<a href="/my-work/{assignment.id}" class="group grid gap-4 rounded-2xl border border-border bg-card p-4 transition hover:border-primary/50 hover:bg-secondary/30 sm:grid-cols-[1fr_auto]" data-testid="technician-assignment-{assignment.id}">
					<div class="min-w-0">
						<div class="flex flex-wrap items-center gap-2">
							<h2 class="truncate font-semibold">{assignment.title}</h2>
							<StatusBadge status={assignment.status} />
							{#if assignment.unreadMessageCount > 0}<span class="rounded-full bg-primary px-2 py-0.5 text-xs font-semibold text-primary-foreground">{assignment.unreadMessageCount} new</span>{/if}
						</div>
						<p class="mt-2 flex items-center gap-1 text-sm text-muted-foreground"><MapPin class="h-4 w-4" />{assignment.address}{assignment.unit ? ` · Unit ${assignment.unit}` : ''}</p>
						<p class="mt-1 flex items-center gap-1 text-sm text-muted-foreground"><CalendarClock class="h-4 w-4" />{when(assignment.scheduledForUtc, assignment.scheduledWindowEndUtc)}</p>
					</div>
					<ChevronRight class="self-center justify-self-end text-muted-foreground transition group-hover:translate-x-0.5 group-hover:text-primary" />
				</a>
			{/each}
		</div>

	{/if}

	{#if !query.isPending && !query.isError}
		<div class="flex items-center justify-between text-sm text-muted-foreground" data-testid="technician-assignment-pagination">
			<span>{totalCount === 0 ? '0 of 0 assignments' : `${rangeStart}–${rangeEnd} of ${totalCount} assignments`}</span>
			<div class="flex items-center gap-2">
				<Button variant="outline" disabled={page <= 1} onclick={() => page -= 1}>Previous</Button>
				<span>Page {page} of {totalPages}</span>
				<Button variant="outline" disabled={page >= totalPages} onclick={() => page += 1}>Next</Button>
			</div>
		</div>
	{/if}
</div>
