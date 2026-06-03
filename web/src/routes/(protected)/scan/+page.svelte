<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { toast } from 'svelte-sonner';
	import { scan, type ScanDraftResponse } from '$lib/api/scan';
	import FileDrop from '$lib/components/FileDrop.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import * as Tabs from '$lib/components/ui/tabs';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';

	const queryClient = useQueryClient();

	// Filter state
	const FILTER_TABS = ['All', 'Pending', 'Reviewing', 'Confirmed'] as const;
	type FilterTab = (typeof FILTER_TABS)[number];
	let activeFilter = $state<FilterTab>('All');

	// Document type the scan should become: 'Expense' (receipt/bill) or
	// 'Payment' (rent check). Drives the LLM extraction + confirm flow.
	// Defaults to 'Expense' since receipts/bills are the most common capture.
	const DOC_TYPES = [
		{ value: 'Expense', label: 'Receipt / Bill', hint: 'Becomes an expense record' },
		{ value: 'Payment', label: 'Rent Check / Payment', hint: 'Becomes a payment record' }
	] as const;
	let docType = $state<string>('Expense');

	const scansQuery = createQuery(() => ({
		queryKey: ['scans', activeFilter],
		queryFn: () => scan.list(activeFilter === 'All' ? undefined : activeFilter)
	}));

	const uploadMutation = createMutation(() => ({
		mutationFn: (file: File) => scan.upload(file, docType),
		onSuccess: (res) => {
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			goto(`/scan/${res.draftId}`);
		},
		onError: (err) => {
			toast.error(err instanceof Error ? err.message : 'Upload failed');
		}
	}));

	function handleFileSelected(file: File) {
		uploadMutation.mutate(file);
	}

	// Scan statuses not in StatusBadge default map — pass a custom map
	const scanStatusMap: Record<string, { label?: string; class: string }> = {
		Pending:    { label: 'Processing', class: 'bg-amber-100 text-amber-800 border-amber-200 dark:bg-amber-900/30 dark:text-amber-300 dark:border-amber-800' },
		Processing: { class: 'bg-amber-100 text-amber-800 border-amber-200 dark:bg-amber-900/30 dark:text-amber-300 dark:border-amber-800' },
		Reviewing:  { label: 'Ready to review', class: 'bg-blue-100 text-blue-800 border-blue-200 dark:bg-blue-900/30 dark:text-blue-300 dark:border-blue-800' },
		Confirmed: { class: 'bg-green-100 text-green-800 border-green-200 dark:bg-green-900/30 dark:text-green-300 dark:border-green-800' },
		Failed:    { class: 'bg-red-100 text-red-800 border-red-200 dark:bg-red-900/30 dark:text-red-300 dark:border-red-800' },
		Rejected:  { class: 'bg-red-100 text-red-800 border-red-200 dark:bg-red-900/30 dark:text-red-300 dark:border-red-800' },
	};

	const columns: ColumnDef<ScanDraftResponse>[] = [
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: statusCellSnippet,
		},
		{
			key: 'targetEntityType',
			title: 'Type',
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

	const draftsList = $derived((scansQuery.data ?? []) as ScanDraftResponse[]);
</script>

{#snippet statusCellSnippet(draft: ScanDraftResponse)}
	<StatusBadge status={draft.status} map={scanStatusMap} />
{/snippet}

{#snippet actionCellSnippet(draft: ScanDraftResponse)}
	<Button variant="link" href="/scan/{draft.id}" class="h-auto p-0">
		Review
	</Button>
{/snippet}

<svelte:head>
	<title>Scan Receipts - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="scan-page">
	<div class="mb-4">
		<h1 class="text-2xl font-bold">Scan a Document</h1>
		<p class="text-sm text-muted-foreground">Upload a photo or PDF and the computer pulls out the details for you to confirm.</p>
	</div>

	<!-- Document type selector -->
	{#if !uploadMutation.isPending}
		<div class="mb-4" data-testid="scan-doc-type">
			<span class="mb-1.5 block text-sm font-medium">What are you scanning?</span>
			<div class="grid max-w-xl grid-cols-2 gap-3">
				{#each DOC_TYPES as opt}
					<button
						type="button"
						data-testid="scan-doc-type-{opt.value}"
						aria-pressed={docType === opt.value}
						onclick={() => { docType = opt.value; }}
						class="flex flex-col items-start gap-0.5 rounded-lg border px-4 py-3 text-left transition-colors {docType === opt.value
							? 'border-accent bg-accent/10 ring-2 ring-accent'
							: 'border-border bg-background hover:bg-muted/50'}"
					>
						<span class="text-sm font-semibold">{opt.label}</span>
						<span class="text-xs text-muted-foreground">{opt.hint}</span>
					</button>
				{/each}
			</div>
		</div>
	{/if}

	<!-- Upload zone -->
	<div class="mb-6" data-testid="scan-upload">
		{#if uploadMutation.isPending}
			<Card.Root class="flex items-center justify-center px-6 py-8 border-2 border-dashed">
				<Card.Content class="p-0">
					<div class="text-center">
						<div class="mx-auto mb-3 h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"></div>
						<p class="text-sm text-muted-foreground">Uploading…</p>
					</div>
				</Card.Content>
			</Card.Root>
		{:else}
			<FileDrop onselected={handleFileSelected} />
		{/if}
	</div>

	<!-- Filter tabs -->
	<Tabs.Root
		value={activeFilter}
		onValueChange={(v) => { if (v) activeFilter = v as FilterTab; }}
		class="mb-4"
	>
		<Tabs.List>
			{#each FILTER_TABS as tab}
				<Tabs.Trigger value={tab}>{tab}</Tabs.Trigger>
			{/each}
		</Tabs.List>

		<!-- Drafts table — rendered once, inside a shared content area -->
		<Tabs.Content value={activeFilter} class="mt-4">
			<DataGrid
				data={draftsList}
				{columns}
				loading={scansQuery.isLoading}
				emptyMessage="No scan drafts found."
				onRowClick={(draft) => goto(`/scan/${draft.id}`)}
				getRowKey={(draft) => draft.id}
				getRowTestId={() => 'scan-row'}
				data-testid="scans-list"
			/>
		</Tabs.Content>
	</Tabs.Root>
</div>
