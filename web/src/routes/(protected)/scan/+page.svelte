<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { scan, type ScanDraftResponse } from '$lib/api/scan';
	import { Button } from '$lib/components/ui/button';
	import * as Tabs from '$lib/components/ui/tabs';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ScanCapturePanel from '$lib/components/scan/ScanCapturePanel.svelte';
	import { Layers } from '@lucide/svelte';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import { parseScanContext } from '$lib/scan/scan-context';
	import { SCAN_HISTORY_FILTERS, formatScanHistoryEmptyMessage, resolveScanHistoryFilter, type ScanHistoryFilter } from '$lib/scans/scan-history-filters';
	import { createdRecordHref } from '$lib/scans/scan-review-state';

	const PAGE_SIZE = 20;

	// Filter state
	const initialParams = page.url.searchParams;
	const initialStatus = readGridParam(initialParams, 'status');
	let activeFilter = $state<ScanHistoryFilter>(resolveScanHistoryFilter(initialStatus));
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));

	let filterResetPrimed = false;
	$effect(() => {
		activeFilter;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		gridPage = 1;
	});

	$effect(() => {
		syncGridUrl({
			status: activeFilter === 'All' ? '' : activeFilter,
			sort: gridSort,
			page: gridPage,
		}, { page: 1 });
	});

	const scanContext = $derived(parseScanContext(page.url.searchParams));

	const scansQuery = createQuery(() => ({
		queryKey: ['scans', activeFilter, 'page', gridSort, gridPage, PAGE_SIZE],
		queryFn: () => scan.listPage(activeFilter === 'All' ? undefined : activeFilter, {
			sort: gridSort || undefined,
			skip: (gridPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
		})
	}));

	// Recent bulk-import batches (lease imports). Shown as quick links back into review.
	const batchesQuery = createQuery(() => ({
		queryKey: ['scan-batches'],
		queryFn: () => scan.listBatches()
	}));
	const recentBatches = $derived((batchesQuery.data ?? []).slice(0, 5));

	// Scan statuses not in StatusBadge default map — pass a custom map
	const scanStatusMap: Record<string, { label?: string; class: string }> = {
		Pending:    { label: 'Processing', class: 'm3-tone-chip border m3-tone--warning' },
		Processing: { class: 'm3-tone-chip border m3-tone--warning' },
		Reviewing:  { label: 'Ready to review', class: 'm3-tone-chip border m3-tone--info' },
		Confirmed: { class: 'm3-tone-chip border m3-tone--success' },
		Failed:    { class: 'm3-tone-chip border m3-tone--error' },
		Rejected:  { class: 'm3-tone-chip border m3-tone--error' },
	};

	const columns: ColumnDef<ScanDraftResponse>[] = [
		{
			key: 'status',
			title: 'Status',
			sortable: true,
			mobileRole: 'badge',
			cell: statusCellSnippet,
		},
		{
			key: 'targetEntityType',
			title: 'Type',
			sortable: true,
			mobileRole: 'subtitle',
			accessor: (d) => d.targetEntityType,
		},
		{
			key: 'createdAt',
			title: 'Created',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'actions',
			title: '',
			mobileRole: 'hidden',
			align: 'right',
			width: '5rem',
			cell: actionCellSnippet,
		},
	];

	const draftsList = $derived((scansQuery.data?.items ?? []) as ScanDraftResponse[]);
	const draftsTotalCount = $derived(scansQuery.data?.totalCount ?? 0);
	const scanHistoryEmptyMessage = $derived(formatScanHistoryEmptyMessage(activeFilter));

	// Where a CONFIRMED draft's created record lives (Payment/Expense/WorkOrder/Lease). Mirrors the
	// review page's linkedRecordHref. Returns null when there is no created record to link to (the
	// caller then falls back to the draft itself).
	function linkedRecordHref(draft: ScanDraftResponse): string | null {
		const type = draft.createdEntityType;
		const id = draft.createdEntityId;
		if (!type || !id) return null;
		// A Loan has no standalone detail page (it lives under its property) — fall back to the
		// read-only draft rather than linking to a non-existent loan record.
		if (type === 'Loan') return null;
		return createdRecordHref(type, id, draft.createdUnitId);
	}

	// A confirmed draft is terminal: its action/row-click should jump straight to the created record
	// (the editable source of truth) rather than the read-only draft.
	function rowHref(draft: ScanDraftResponse): string {
		if (draft.status === 'Confirmed') {
			return linkedRecordHref(draft) ?? `/scan/${draft.id}`;
		}
		return `/scan/${draft.id}`;
	}
</script>

{#snippet statusCellSnippet(draft: ScanDraftResponse)}
	<StatusBadge status={draft.status} map={scanStatusMap} />
{/snippet}

{#snippet actionCellSnippet(draft: ScanDraftResponse)}
	{#if draft.status === 'Confirmed' && linkedRecordHref(draft)}
		<!-- Confirmed: link straight to the created record, not the read-only draft. -->
		<Button variant="link" href={linkedRecordHref(draft)} class="h-auto p-0" data-testid="scan-view-record">
			View record
		</Button>
	{:else}
		<Button variant="link" href="/scan/{draft.id}" class="h-auto p-0">
			Review
		</Button>
	{/if}
{/snippet}

<svelte:head>
	<title>Scan / Add - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="scan-page">
	<div class="mb-4 flex flex-wrap items-start justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Scan / Add</h1>
			<p class="text-sm text-muted-foreground">Upload a photo or PDF and the computer pulls out the details for you to confirm.</p>
		</div>
		<Button variant="outline" class="gap-2" href="/scan/batch" data-testid="scan-bulk-import-leases">
			<Layers class="h-4 w-4" />
			Bulk import leases
		</Button>
	</div>

	<!-- Primary front door: guided, pre-filled new-rental-from-your-lease flow -->
	<a href="/scan/new-rental" class="mb-6 block rounded-lg border border-accent/40 bg-accent/5 p-4 hover:bg-accent/10" data-testid="scan-new-rental-cta">
		<p class="text-sm font-semibold text-foreground">New rental from your lease</p>
		<p class="text-xs text-muted-foreground">Snap or upload a lease → we pre-fill the property, unit, tenant, and lease for you to review.</p>
	</a>

	<div class="mb-6 border-b pb-6">
		<ScanCapturePanel context={scanContext} />
	</div>

	<!-- Recent lease imports -->
	{#if recentBatches.length > 0}
		<div class="mb-6" data-testid="scan-recent-batches">
			<h2 class="mb-2 text-sm font-semibold">Recent lease imports</h2>
			<div class="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
				{#each recentBatches as batch (batch.id)}
					<a
						href="/scan/batch/{batch.id}"
						class="flex items-center justify-between gap-3 rounded-lg border border-border bg-card px-4 py-3 transition-colors hover:bg-secondary"
						data-testid="scan-batch-row"
					>
						<div class="min-w-0">
							<p class="truncate text-sm font-medium text-foreground">
								{batch.name || `Import #${batch.id}`}
							</p>
							<p class="text-xs text-muted-foreground">
								{batch.counts.confirmed} of {batch.counts.total} created
							</p>
						</div>
						<StatusBadge status={batch.status} />
					</a>
				{/each}
			</div>
		</div>
	{/if}

	<!-- Filter tabs -->
	<Tabs.Root
		value={activeFilter}
		onValueChange={(v) => { if (v) activeFilter = v as ScanHistoryFilter; }}
		class="mb-4"
	>
		<Tabs.List>
			{#each SCAN_HISTORY_FILTERS as tab}
				<Tabs.Trigger value={tab}>{tab}</Tabs.Trigger>
			{/each}
		</Tabs.List>

		<!-- Drafts table — rendered once, inside a shared content area -->
		<Tabs.Content value={activeFilter} class="mt-4">
			<DataGrid
				data={draftsList}
				{columns}
				loading={scansQuery.isLoading || scansQuery.isFetching}
				emptyMessage={scanHistoryEmptyMessage}
				onRowClick={(draft) => goto(rowHref(draft))}
				getRowKey={(draft) => draft.id}
				getRowTestId={() => 'scan-row'}
				data-testid="scans-list"
				pageSize={PAGE_SIZE}
				page={gridPage}
				totalCount={draftsTotalCount}
				serverSide
				onPageChange={(page) => (gridPage = page)}
				sort={gridSort}
				onSortChange={(s) => { gridSort = s ?? ''; gridPage = 1; }}
			/>
		</Tabs.Content>
	</Tabs.Root>
</div>
