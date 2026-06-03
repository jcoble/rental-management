<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { toast } from 'svelte-sonner';
	import { scan, type ScanDraftResponse } from '$lib/api/scan';
	import FileDrop from '$lib/components/FileDrop.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Badge } from '$lib/components/ui/badge';
	import { Button } from '$lib/components/ui/button';
	import * as Table from '$lib/components/ui/table';
	import * as Tabs from '$lib/components/ui/tabs';

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

	function statusBadgeVariant(status: string): 'default' | 'secondary' | 'destructive' | 'outline' {
		switch (status) {
			case 'Confirmed':
				return 'default';
			case 'Reviewing':
				return 'secondary';
			case 'Failed':
			case 'Rejected':
				return 'destructive';
			default:
				return 'outline';
		}
	}

	// Keep custom colour classes for statuses that don't map to a variant cleanly
	function statusBadgeClass(status: string): string {
		switch (status) {
			case 'Pending':
				return 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300 border-amber-200 dark:border-amber-800';
			case 'Reviewing':
				return 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300 border-blue-200 dark:border-blue-800';
			case 'Confirmed':
				return 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300 border-green-200 dark:border-green-800';
			case 'Failed':
			case 'Rejected':
				return 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-300 border-red-200 dark:border-red-800';
			default:
				return '';
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
			{#if scansQuery.isLoading}
				<p class="py-8 text-center text-sm text-muted-foreground">Loading…</p>
			{:else if draftsList.length === 0}
				<p class="py-8 text-center text-sm text-muted-foreground">No scan drafts found.</p>
			{:else}
				<Card.Root class="overflow-hidden py-0 gap-0">
					<Table.Root>
						<Table.Header>
							<Table.Row>
								<Table.Head class="px-4 py-3">Status</Table.Head>
								<Table.Head class="px-4 py-3">Type</Table.Head>
								<Table.Head class="px-4 py-3">Created</Table.Head>
								<Table.Head class="px-4 py-3">Action</Table.Head>
							</Table.Row>
						</Table.Header>
						<Table.Body>
							{#each draftsList as draft (draft.id)}
								<Table.Row data-testid="scan-row" data-draft-id={draft.id}>
									<Table.Cell class="px-4 py-3">
										<Badge variant="outline" class={statusBadgeClass(draft.status)}>
											{draft.status}
										</Badge>
									</Table.Cell>
									<Table.Cell class="px-4 py-3 text-muted-foreground">{draft.targetEntityType}</Table.Cell>
									<Table.Cell class="px-4 py-3 text-muted-foreground">
										{new Date(draft.createdAt).toLocaleDateString()}
									</Table.Cell>
									<Table.Cell class="px-4 py-3">
										<div class="flex items-center gap-3">
											<Button variant="link" href="/scan/{draft.id}" class="h-auto p-0">
												Review
											</Button>
											{#if draft.createdEntityType === 'Expense' && draft.createdEntityId}
												<Button
													data-testid="scan-expense-link"
													variant="link"
													href="/accounting/expenses/{draft.createdEntityId}"
													class="h-auto p-0"
												>
													View Expense
												</Button>
											{/if}
										</div>
									</Table.Cell>
								</Table.Row>
							{/each}
						</Table.Body>
					</Table.Root>
				</Card.Root>
			{/if}
		</Tabs.Content>
	</Tabs.Root>
</div>
