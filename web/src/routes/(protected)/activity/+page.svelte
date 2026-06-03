<script lang="ts">
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { activity } from '$lib/api/endpoints/activity';
	import type { ActivityLog } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { debounced } from '$lib/utils/debounce.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { RefreshCw } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 50;

	const ACTIVITY_TYPES = [
		'PortfolioCreated',
		'PropertyCreated',
		'UnitCreated',
		'TenantCreated',
		'LeaseCreated',
		'LeaseStatusChanged',
		'PaymentRecorded',
		'ExpenseRecorded',
		'WorkOrderCreated',
		'WorkOrderStatusChanged',
		'AppointmentScheduled',
		'InspectionScheduled',
		'Note',
	] as const;

	const ENTITY_TYPES = [
		'Portfolio',
		'Property',
		'Unit',
		'Tenant',
		'Lease',
		'Payment',
		'Expense',
		'WorkOrder',
		'Appointment',
		'Inspection',
	] as const;

	let search = $state('');
	let typeFilter = $state('');
	let entityTypeFilter = $state('');
	let skip = $state(0);

	const debouncedSearch = debounced(() => search, 300);

	// Reset pagination when filters change
	$effect(() => {
		debouncedSearch.value;
		typeFilter;
		entityTypeFilter;
		skip = 0;
	});

	const activityQuery = createQuery(() => ({
		queryKey: ['activity', portfolioId, debouncedSearch.value, typeFilter, entityTypeFilter, skip],
		queryFn: () =>
			activity.list(portfolioId, {
				search: debouncedSearch.value || undefined,
				type: typeFilter || undefined,
				entityType: entityTypeFilter || undefined,
				skip,
				take: PAGE_SIZE,
				sort: '-createdAt',
			}),
	}));

	function refresh() {
		queryClient.invalidateQueries({ queryKey: ['activity', portfolioId] });
	}

	const entries = $derived(activityQuery.data ?? []);

	// --- Display helpers ---

	/** Format timestamp as "May 30, 2026 at 3:42 PM" */
	function formatAbsolute(iso: string): string {
		return new Date(iso).toLocaleString('en-US', {
			month: 'short',
			day: 'numeric',
			year: 'numeric',
			hour: 'numeric',
			minute: '2-digit',
		});
	}

	/** Relative time: "2 minutes ago", "3 days ago", etc. */
	function formatRelative(iso: string): string {
		const diffMs = Date.now() - new Date(iso).getTime();
		const diffSec = Math.floor(diffMs / 1000);
		if (diffSec < 60) return 'just now';
		const diffMin = Math.floor(diffSec / 60);
		if (diffMin < 60) return `${diffMin}m ago`;
		const diffHr = Math.floor(diffMin / 60);
		if (diffHr < 24) return `${diffHr}h ago`;
		const diffDay = Math.floor(diffHr / 24);
		if (diffDay < 30) return `${diffDay}d ago`;
		return formatAbsolute(iso);
	}

	/** Dot color by entity type */
	function dotColor(entry: ActivityLog): string {
		switch (entry.entityType?.toLowerCase()) {
			case 'lease':
				return 'bg-blue-500';
			case 'payment':
				return 'bg-green-500';
			case 'expense':
				return 'bg-orange-500';
			case 'workorder':
				return 'bg-yellow-500';
			case 'tenant':
				return 'bg-purple-500';
			case 'property':
			case 'unit':
				return 'bg-slate-500';
			case 'appointment':
			case 'inspection':
				return 'bg-cyan-500';
			default:
				return 'bg-muted-foreground';
		}
	}

	/** Human-readable label for entity type + id */
	function entityLabel(entry: ActivityLog): string {
		if (!entry.entityType) return '';
		return entry.entityId ? `${entry.entityType} #${entry.entityId}` : entry.entityType;
	}

	/** Display text from description, action, or typeName fallback */
	function entryTitle(entry: ActivityLog): string {
		if (entry.description) return entry.description;
		if (entry.action) return entry.action;
		return entry.typeName ?? entry.type;
	}
</script>

<svelte:head>
	<title>Activity - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="activity-page">
	<div class="mb-4 flex items-center justify-between">
		<div>
			<h1 class="text-2xl font-bold">Activity</h1>
			<p class="text-sm text-muted-foreground">Audit log of all portfolio events.</p>
		</div>
		<Button
			variant="outline"
			size="sm"
			onclick={refresh}
			disabled={activityQuery.isFetching}
			data-testid="activity-refresh"
			aria-label="Refresh activity feed"
		>
			<RefreshCw class="h-3.5 w-3.5 {activityQuery.isFetching ? 'animate-spin' : ''}" />
			Refresh
		</Button>
	</div>

	<Card.Root class="gap-0 py-0">
		<!-- Filters bar -->
		<div class="flex flex-wrap items-center gap-2 border-b border-border px-3 py-2">
			<div class="min-w-[180px] flex-1">
				<SearchInput bind:value={search} placeholder="Search activity…" testid="activity-search" />
			</div>
			<Select.Root type="single" bind:value={typeFilter}>
				<Select.Trigger class="w-44" data-testid="activity-type-filter">
					{typeFilter || 'All types'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="All types">All types</Select.Item>
					{#each ACTIVITY_TYPES as t}
						<Select.Item value={t} label={t}>{t}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<Select.Root type="single" bind:value={entityTypeFilter}>
				<Select.Trigger class="w-40" data-testid="activity-entity-type-filter">
					{entityTypeFilter || 'All entities'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="All entities">All entities</Select.Item>
					{#each ENTITY_TYPES as e}
						<Select.Item value={e} label={e}>{e}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
		</div>

		<!-- Entry list -->
		<Card.Content class="p-0" data-testid="activity-list">
			{#if activityQuery.isLoading}
				<p class="py-10 text-center text-sm text-muted-foreground" data-testid="activity-loading">
					Loading…
				</p>
			{:else if activityQuery.isError}
				<p class="py-10 text-center text-sm text-destructive" data-testid="activity-error">
					Failed to load activity. Try refreshing.
				</p>
			{:else if entries.length === 0}
				<p class="py-10 text-center text-sm text-muted-foreground" data-testid="activity-empty">
					No activity found.
				</p>
			{:else}
				<ul class="divide-y divide-border" role="list">
					{#each entries as entry (entry.id)}
						<li
							class="flex items-start gap-3 px-4 py-3 hover:bg-muted/40"
							data-testid={entry.testId ?? `activity-${entry.id}`}
						>
							<!-- Colored dot -->
							<span
								class="mt-1.5 h-2.5 w-2.5 shrink-0 rounded-full {dotColor(entry)}"
								aria-hidden="true"
							></span>

							<!-- Main content -->
							<div class="min-w-0 flex-1">
								<p class="text-sm font-medium leading-snug">{entryTitle(entry)}</p>
								<div class="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-xs text-muted-foreground">
									{#if entry.actor}
										<span>{entry.actor}</span>
										<span aria-hidden="true">·</span>
									{/if}
									{#if entry.entityType}
										<span class="rounded bg-muted px-1 py-0.5 font-mono text-[11px]">{entityLabel(entry)}</span>
										<span aria-hidden="true">·</span>
									{/if}
									<span class="font-medium text-foreground/60">{entry.typeName ?? entry.type}</span>
								</div>
							</div>

							<!-- Timestamp -->
							<time
								datetime={entry.createdAt}
								title={formatAbsolute(entry.createdAt)}
								class="shrink-0 text-xs text-muted-foreground"
							>
								{formatRelative(entry.createdAt)}
							</time>
						</li>
					{/each}
				</ul>
			{/if}
		</Card.Content>

		<!-- Pagination -->
		<Card.Footer class="border-t border-border px-3 py-2">
			<Pagination bind:skip take={PAGE_SIZE} count={entries.length} testid="activity-pagination" />
		</Card.Footer>
	</Card.Root>
</div>
