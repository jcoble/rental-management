<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { untrack } from 'svelte';
	import type { UnitDashboard } from '$lib/types';
	import { applications, type ApplicationResponse } from '$lib/api/endpoints/applications';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ApplicationDetail from '$lib/components/records/ApplicationDetail.svelte';
	import { Button } from '$lib/components/ui/button';
	import { ArrowLeft, ClipboardList } from '@lucide/svelte';

	let { dashboard }: { dashboard: UnitDashboard } = $props();

	const portfolioId = $derived(getCurrentPortfolioId());
	const unitId = $derived(dashboard.unit.id);

	// A selected application folds its full detail inline (?app=<id> on the unit URL); otherwise the list shows.
	const selectedApp = $derived(Number(page.url.searchParams.get('app')) || null);

	const TAB_PAGE_SIZE = 20;
	let appPage = $state(1);
	let appItems = $state<ApplicationResponse[]>([]);

	// Applications tied to THIS unit only — filtered DB-side via the API's ?unitId= filter.
	const applicationsQuery = createQuery(() => ({
		queryKey: ['unit-applications', portfolioId, unitId, appPage, TAB_PAGE_SIZE],
		enabled: portfolioId > 0 && unitId > 0,
		queryFn: () => applications.listPage({
			unitId,
			skip: (appPage - 1) * TAB_PAGE_SIZE,
			take: TAB_PAGE_SIZE,
		}),
	}));

	// Reset accumulation when the unit changes.
	$effect(() => {
		void unitId;
		appPage = 1;
		appItems = [];
	});

	// Accumulate pages (first page replaces; later pages append de-duped) so "Load more" grows the list.
	$effect(() => {
		const data = applicationsQuery.data;
		if (!data) return;
		if (data.skip === 0) {
			appItems = data.items;
			return;
		}
		const current = untrack(() => appItems);
		const seen = new Set(current.map((a) => a.id));
		appItems = [...current, ...data.items.filter((a) => !seen.has(a.id))];
	});

	const appList = $derived(appItems);
	const totalApps = $derived(applicationsQuery.data?.totalCount ?? appItems.length);
	const hasMore = $derived(appItems.length < totalApps);

	const STATUS_MAP = {
		Submitted: { label: 'Submitted', class: 'm3-tone-chip border m3-tone--info' },
		UnderReview: { label: 'Under Review', class: 'm3-tone-chip border m3-tone--primary' },
		Approved: { label: 'Approved', class: 'm3-tone-chip border m3-tone--success' },
		Declined: { label: 'Declined', class: 'm3-tone-chip border m3-tone--error' },
		Withdrawn: { label: 'Withdrawn', class: 'bg-muted text-muted-foreground border-border' },
	};

	function fmtDate(value: string | null | undefined): string {
		if (!value) return '—';
		const d = new Date(value);
		return isNaN(d.getTime()) ? '—' : d.toLocaleDateString(undefined, { timeZone: 'UTC' });
	}

	function loadMore() {
		if (applicationsQuery.isFetching || !hasMore) return;
		appPage += 1;
	}

	// Selecting a row is a real navigation step (no replaceState) so Back returns to the list.
	function openApp(id: number) {
		goto('/units/' + unitId + '?tab=applications&app=' + id, { keepFocus: true, noScroll: true });
	}

	// Clearing the selection drops ?app= (replaceState — it's a peer of the list, not a new history step).
	function clearSelection() {
		goto('/units/' + unitId + '?tab=applications', { replaceState: true, keepFocus: true, noScroll: true });
	}
</script>

<div class="space-y-4" data-testid="unit-applications-tab">
{#if selectedApp}
	<!-- Folded application detail: the same <ApplicationDetail> the generic /applications/[id] page mounts. -->
	<Button variant="outline" size="sm" class="gap-1" onclick={clearSelection} data-testid="application-back-to-list">
		<ArrowLeft class="h-4 w-4" /> Back to applications
	</Button>
	<ApplicationDetail applicationId={selectedApp} onDeleted={clearSelection} />
{:else if applicationsQuery.isLoading}
	<p class="rounded-xl border bg-card p-6 text-center text-sm text-muted-foreground">Loading applications…</p>
{:else if appList.length === 0}
	<DetailCard title="No applications" icon={ClipboardList} accent="muted" testid="unit-applications-empty">
		<p class="text-sm text-muted-foreground">No applications tied to this unit yet.</p>
	</DetailCard>
{:else}
	<ul class="space-y-2" data-testid="unit-applications-list">
		{#each appList as a (a.id)}
			<li class="rounded-xl border bg-card">
				<button
					type="button"
					class="flex w-full items-center justify-between gap-3 p-3 text-left transition hover:bg-muted/40"
					onclick={() => openApp(a.id)}
					data-testid="unit-application-{a.id}"
				>
					<span class="min-w-0">
						<span class="block truncate text-sm font-medium">{a.firstName} {a.lastName}</span>
						<span class="mt-0.5 block text-xs text-muted-foreground">Submitted {fmtDate(a.submittedAtUtc)}</span>
					</span>
					<StatusBadge status={a.status} map={STATUS_MAP} />
				</button>
			</li>
		{/each}
	</ul>
	{#if hasMore}
		<div class="flex justify-center">
			<Button variant="outline" size="sm" onclick={loadMore} disabled={applicationsQuery.isFetching} data-testid="unit-applications-load-more">
				{applicationsQuery.isFetching ? 'Loading…' : 'Load more'}
			</Button>
		</div>
	{/if}
{/if}
</div>
