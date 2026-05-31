<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { toast } from 'svelte-sonner';
	import { scan, type ScanDraftResponse } from '$lib/api/scan';
	import FileDrop from '$lib/components/FileDrop.svelte';

	const queryClient = useQueryClient();

	// Filter state
	const FILTER_TABS = ['All', 'Pending', 'Reviewing', 'Confirmed'] as const;
	type FilterTab = (typeof FILTER_TABS)[number];
	let activeFilter = $state<FilterTab>('All');

	const scansQuery = createQuery(() => ({
		queryKey: ['scans', activeFilter],
		queryFn: () => scan.list(activeFilter === 'All' ? undefined : activeFilter)
	}));

	const uploadMutation = createMutation(() => ({
		mutationFn: (file: File) => scan.upload(file, 'Expense'),
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

	function statusBadgeClass(status: string): string {
		switch (status) {
			case 'Pending':
				return 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300';
			case 'Reviewing':
				return 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300';
			case 'Confirmed':
				return 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300';
			case 'Failed':
			case 'Rejected':
				return 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-300';
			default:
				return 'bg-card text-muted-foreground';
		}
	}

	const draftsList = $derived((scansQuery.data ?? []) as ScanDraftResponse[]);
</script>

<svelte:head>
	<title>Scan Receipts - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="scan-page">
	<div class="mb-4">
		<h1 class="text-2xl font-bold">Scan Receipts</h1>
		<p class="text-sm text-muted-foreground">Upload a receipt or invoice to extract and create an expense record.</p>
	</div>

	<!-- Upload zone -->
	<div class="mb-6" data-testid="scan-upload">
		{#if uploadMutation.isPending}
			<div class="flex items-center justify-center rounded-lg border-2 border-dashed border-border bg-card px-6 py-8">
				<div class="text-center">
					<div class="mx-auto mb-3 h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"></div>
					<p class="text-sm text-muted-foreground">Uploading…</p>
				</div>
			</div>
		{:else}
			<FileDrop onselected={handleFileSelected} />
		{/if}
	</div>

	<!-- Filter tabs -->
	<div class="mb-4 flex gap-1 border-b border-border">
		{#each FILTER_TABS as tab}
			<button
				class="rounded-t px-4 py-2 text-sm font-medium transition-colors
					{activeFilter === tab
					? 'border-b-2 border-accent text-primary'
					: 'text-muted-foreground hover:text-foreground'}"
				onclick={() => (activeFilter = tab)}
			>
				{tab}
			</button>
		{/each}
	</div>

	<!-- Drafts table -->
	{#if scansQuery.isLoading}
		<p class="py-8 text-center text-sm text-muted-foreground">Loading…</p>
	{:else if draftsList.length === 0}
		<p class="py-8 text-center text-sm text-muted-foreground">No scan drafts found.</p>
	{:else}
		<div class="overflow-hidden rounded-lg border border-border bg-card">
			<table class="w-full text-sm">
				<thead class="border-b border-border bg-background">
					<tr>
						<th class="px-4 py-3 text-left font-medium text-muted-foreground">Status</th>
						<th class="px-4 py-3 text-left font-medium text-muted-foreground">Type</th>
						<th class="px-4 py-3 text-left font-medium text-muted-foreground">Created</th>
						<th class="px-4 py-3 text-left font-medium text-muted-foreground">Action</th>
					</tr>
				</thead>
				<tbody class="divide-y divide-border">
					{#each draftsList as draft (draft.id)}
						<tr class="hover:bg-secondary" data-testid="scan-row" data-draft-id={draft.id}>
							<td class="px-4 py-3">
								<span class="inline-flex rounded-full px-2 py-0.5 text-xs font-medium {statusBadgeClass(draft.status)}">
									{draft.status}
								</span>
							</td>
							<td class="px-4 py-3 text-muted-foreground">{draft.targetEntityType}</td>
							<td class="px-4 py-3 text-muted-foreground">
								{new Date(draft.createdAt).toLocaleDateString()}
							</td>
							<td class="px-4 py-3">
								<a
									href="/scan/{draft.id}"
									class="text-primary hover:underline"
								>
									Review
								</a>
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	{/if}
</div>
