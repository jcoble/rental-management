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
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import { Mic, Square, Layers } from '@lucide/svelte';

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
		{ value: 'Payment', label: 'Rent Check / Payment', hint: 'Becomes a payment record' },
		{ value: 'WorkOrder', label: 'Maintenance Request', hint: 'Becomes a work order' },
		{ value: 'Lease', label: 'Lease Agreement', hint: 'Becomes a lease record' }
	] as const;
	let docType = $state<string>('Expense');
	let isRecording = $state(false);
	let recorder: MediaRecorder | null = null;
	let voiceChunks: Blob[] = [];

	const scansQuery = createQuery(() => ({
		queryKey: ['scans', activeFilter],
		queryFn: () => scan.list(activeFilter === 'All' ? undefined : activeFilter)
	}));

	// Recent bulk-import batches (lease imports). Shown as quick links back into review.
	const batchesQuery = createQuery(() => ({
		queryKey: ['scan-batches'],
		queryFn: () => scan.listBatches()
	}));
	const recentBatches = $derived((batchesQuery.data ?? []).slice(0, 5));

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

	const voiceMutation = createMutation(() => ({
		mutationFn: (audio: Blob) => scan.createVoiceDraft(audio),
		onSuccess: (draft) => {
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			goto(`/scan/${draft.id}`);
		},
		onError: (err) => {
			toast.error(err instanceof Error ? err.message : 'Voice capture failed');
		}
	}));

	function handleFileSelected(file: File) {
		uploadMutation.mutate(file);
	}

	async function startVoiceCapture() {
		if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined') {
			toast.error('Voice recording is not available in this browser.');
			return;
		}

		try {
			const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
			voiceChunks = [];
			const nextRecorder = new MediaRecorder(stream);
			nextRecorder.ondataavailable = (event) => {
				if (event.data.size > 0) voiceChunks.push(event.data);
			};
			nextRecorder.onstop = () => {
				stream.getTracks().forEach((track) => track.stop());
				const audio = new Blob(voiceChunks, { type: nextRecorder.mimeType || 'audio/webm' });
				voiceMutation.mutate(audio);
				recorder = null;
			};
			recorder = nextRecorder;
			nextRecorder.start();
			isRecording = true;
		} catch (err) {
			toast.error(err instanceof Error ? err.message : 'Could not start recording');
		}
	}

	function stopVoiceCapture() {
		if (!recorder || recorder.state === 'inactive') return;
		isRecording = false;
		recorder.stop();
	}

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

	// Where a CONFIRMED draft's created record lives (Payment/Expense/WorkOrder/Lease). Mirrors the
	// review page's linkedRecordHref. Returns null when there is no created record to link to (the
	// caller then falls back to the draft itself).
	function linkedRecordHref(draft: ScanDraftResponse): string | null {
		const type = draft.createdEntityType;
		const id = draft.createdEntityId;
		if (!type || !id) return null;
		if (type === 'Payment') return `/accounting/payments/${id}`;
		if (type === 'WorkOrder') return `/maintenance/${id}`;
		if (type === 'Lease') return `/leases/${id}`;
		return `/accounting/expenses/${id}`;
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

	<!-- Document type selector -->
	{#if !uploadMutation.isPending}
		<div class="mb-4" data-testid="scan-doc-type">
			<div class="mb-1.5 flex items-center gap-1.5">
				<span class="block text-sm font-medium">What are you scanning?</span>
				<HelpPopover
					title="Pick the kind of document"
					summary="Tell us what you're uploading and we turn it into the right draft — a Receipt or Bill becomes an expense, a Rent Check becomes a payment, and so on."
					detail="The computer reads the document and fills in the fields; you just review and confirm the draft."
					learnMoreUrl={undefined}
				/>
			</div>
			<div class="grid max-w-3xl gap-3 sm:grid-cols-2 lg:grid-cols-4">
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

	<div class="mb-6 flex flex-wrap items-center gap-3 border-b pb-6" data-testid="voice-capture">
		<Button
			type="button"
			variant={isRecording ? 'destructive' : 'outline'}
			class="gap-2"
			disabled={voiceMutation.isPending}
			onclick={isRecording ? stopVoiceCapture : startVoiceCapture}
			data-testid="voice-record-button"
		>
			{#if isRecording}
				<Square class="h-4 w-4" />
				Stop recording
			{:else}
				<Mic class="h-4 w-4" />
				Record voice note
			{/if}
		</Button>
		<HelpPopover
			title="Record voice note"
			summary="Speak instead of type. Describe an expense or repair out loud and we transcribe it, then pull out a draft for you to confirm."
			detail="Great for capturing a bill or a maintenance request on the go without filling in a form."
			learnMoreUrl={undefined}
		/>
		{#if voiceMutation.isPending}
			<span class="text-sm text-muted-foreground">Creating draft...</span>
		{:else if isRecording}
			<span class="text-sm text-muted-foreground">Recording...</span>
		{/if}
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
				onRowClick={(draft) => goto(rowHref(draft))}
				getRowKey={(draft) => draft.id}
				getRowTestId={() => 'scan-row'}
				data-testid="scans-list"
			/>
		</Tabs.Content>
	</Tabs.Root>
</div>
