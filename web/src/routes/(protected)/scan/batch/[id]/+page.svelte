<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { page } from '$app/stores';
	import { goto } from '$app/navigation';
	import { scan, type ScanBatchDraft } from '$lib/api/scan';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import Progress from '$lib/components/ui/Progress.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import { CheckCircle2 } from '@lucide/svelte';

	const batchId = $derived(parseInt($page.params.id ?? '0', 10));

	// Draft statuses don't all exist in StatusBadge's default map — supply a custom one.
	const draftStatusMap: Record<string, { label?: string; class: string }> = {
		Pending: {
			label: 'Reading…',
			class: 'bg-amber-100 text-amber-800 border-amber-200 dark:bg-amber-900/30 dark:text-amber-300 dark:border-amber-800'
		},
		Processing: {
			label: 'Reading…',
			class: 'bg-amber-100 text-amber-800 border-amber-200 dark:bg-amber-900/30 dark:text-amber-300 dark:border-amber-800'
		},
		Reviewing: {
			label: 'Ready to review',
			class: 'bg-blue-100 text-blue-800 border-blue-200 dark:bg-blue-900/30 dark:text-blue-300 dark:border-blue-800'
		},
		Confirmed: {
			label: 'Lease created',
			class: 'bg-green-100 text-green-800 border-green-200 dark:bg-green-900/30 dark:text-green-300 dark:border-green-800'
		},
		Rejected: {
			label: 'Skipped',
			class: 'bg-muted text-muted-foreground border-border'
		},
		Failed: {
			label: 'Could not read',
			class: 'bg-red-100 text-red-800 border-red-200 dark:bg-red-900/30 dark:text-red-300 dark:border-red-800'
		}
	};

	const PROCESSING_STATUSES = new Set(['Pending', 'Processing']);
	const DONE_STATUSES = new Set(['Confirmed', 'Rejected', 'Failed']);

	const batchQuery = createQuery(() => ({
		queryKey: ['scan-batches', batchId],
		queryFn: () => scan.getBatch(batchId),
		// Poll while any draft is still being read by the LLM.
		refetchInterval: (query) => {
			const data = query.state.data;
			if (!data) return 2000;
			const stillProcessing = data.drafts.some((d) => PROCESSING_STATUSES.has(d.status));
			return stillProcessing ? 2000 : false;
		}
	}));

	const batch = $derived(batchQuery.data);
	const drafts = $derived(batch?.drafts ?? []);
	const counts = $derived(batch?.counts);
	const total = $derived(counts?.total ?? drafts.length);
	const confirmedCount = $derived(counts?.confirmed ?? 0);
	const settledCount = $derived(drafts.filter((d) => DONE_STATUSES.has(d.status)).length);
	const reviewingCount = $derived(drafts.filter((d) => d.status === 'Reviewing').length);
	const allSettled = $derived(total > 0 && settledCount >= total);

	const columns: ColumnDef<ScanBatchDraft>[] = [
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: statusCellSnippet
		},
		{
			key: 'tenant',
			title: 'Tenant',
			mobileRole: 'title',
			accessor: (d) => d.tenant ?? '—'
		},
		{
			key: 'unit',
			title: 'Unit',
			mobileRole: 'subtitle',
			accessor: (d) => d.unit ?? '—'
		},
		{
			key: 'term',
			title: 'Term',
			mobileRole: 'meta',
			accessor: (d) => d.term ?? '—'
		},
		{
			key: 'actions',
			title: '',
			mobileRole: 'hidden',
			align: 'right',
			width: '7rem',
			cell: actionCellSnippet
		}
	];

	function rowHref(draft: ScanBatchDraft): string | null {
		if (draft.status === 'Reviewing') return `/scan/${draft.id}`;
		if (draft.status === 'Confirmed' && draft.createdEntityId != null) {
			return `/leases/${draft.createdEntityId}`;
		}
		return null;
	}

	function onRowClick(draft: ScanBatchDraft) {
		const href = rowHref(draft);
		if (href) goto(href);
	}
</script>

{#snippet statusCellSnippet(draft: ScanBatchDraft)}
	<StatusBadge status={draft.status} map={draftStatusMap} />
{/snippet}

{#snippet actionCellSnippet(draft: ScanBatchDraft)}
	{#if draft.status === 'Reviewing'}
		<Button variant="link" href="/scan/{draft.id}" class="h-auto p-0" data-testid="batch-draft-review">
			Review
		</Button>
	{:else if draft.status === 'Confirmed' && draft.createdEntityId != null}
		<Button
			variant="link"
			href="/leases/{draft.createdEntityId}"
			class="h-auto p-0"
			data-testid="batch-draft-view-lease"
		>
			View lease
		</Button>
	{:else if draft.status === 'Failed'}
		<span class="text-xs text-muted-foreground" data-testid="batch-draft-failed">Couldn't read</span>
	{:else if draft.status === 'Rejected'}
		<span class="text-xs text-muted-foreground" data-testid="batch-draft-skipped">Skipped</span>
	{:else}
		<span class="text-xs text-muted-foreground" data-testid="batch-draft-processing">Reading…</span>
	{/if}
{/snippet}

<svelte:head>
	<title>Import progress - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="scan-batch-review-page">
	<div class="mb-4">
		<PageBreadcrumb
			crumbs={[
				{ label: 'Scan', href: '/scan' },
				{ label: 'Import leases', href: '/scan/batch' },
				{ label: batch?.name || `Batch #${batchId}` }
			]}
		/>
	</div>

	<div class="mb-6">
		<h1 class="text-2xl font-bold">{batch?.name || 'Lease import'}</h1>
		<p class="text-sm text-muted-foreground">
			Review each lease the computer read, then confirm it to create the record.
		</p>
	</div>

	{#if batchQuery.isLoading}
		<Card.Root>
			<Card.Content class="flex items-center justify-center px-6 py-10">
				<div
					class="h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"
				></div>
			</Card.Content>
		</Card.Root>
	{:else if batchQuery.isError}
		<Card.Root>
			<Card.Content class="px-6 py-10 text-center">
				<p class="text-sm text-destructive">We couldn't load this import. Please try again.</p>
				<Button variant="outline" class="mt-4" href="/scan/batch">Back to import</Button>
			</Card.Content>
		</Card.Root>
	{:else}
		<!-- Progress summary -->
		<Card.Root class="mb-4" data-testid="batch-progress">
			<Card.Content class="p-5">
				<div class="mb-2 flex items-center justify-between text-sm">
					<span class="font-medium" data-testid="batch-progress-label">
						{confirmedCount} of {total} lease{total === 1 ? '' : 's'} created
					</span>
					{#if reviewingCount > 0}
						<span class="text-muted-foreground" data-testid="batch-progress-reviewing">
							{reviewingCount} ready to review
						</span>
					{/if}
				</div>
				<Progress value={settledCount} max={Math.max(total, 1)} />
				{#if !allSettled && drafts.some((d) => PROCESSING_STATUSES.has(d.status))}
					<p class="mt-2 text-xs text-muted-foreground">
						Still reading some files — this page updates on its own.
					</p>
				{/if}
			</Card.Content>
		</Card.Root>

		{#if allSettled}
			<Card.Root class="mb-4 border-success/40 bg-success/10" data-testid="batch-done">
				<Card.Content class="flex items-center gap-3 p-4">
					<CheckCircle2 class="h-6 w-6 shrink-0 text-success" />
					<div class="flex-1">
						<p class="text-sm font-medium text-foreground">All done!</p>
						<p class="text-xs text-muted-foreground">
							Every lease in this batch has been reviewed.
						</p>
					</div>
					<Button href="/leases" data-testid="batch-done-leases">View leases</Button>
				</Card.Content>
			</Card.Root>
		{/if}

		<!-- Draft queue -->
		<DataGrid
			data={drafts}
			{columns}
			loading={false}
			emptyMessage="No leases in this import."
			{onRowClick}
			getRowKey={(d) => d.id}
			getRowTestId={() => 'batch-draft-row'}
			data-testid="batch-drafts-list"
		/>
	{/if}
</div>
