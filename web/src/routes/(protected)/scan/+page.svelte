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
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import { appendScanContext, parseScanContext, type ScanDocType } from '$lib/scan/scan-context';
	import { scanUploadCopy } from '$lib/scan/scan-copy';
	import { prepareScanDocumentUpload } from '$lib/scan/scan-upload';
	import {
		VOICE_MAX_AUDIO_BYTES,
		VOICE_MAX_RECORDING_SECONDS,
		formatRecordingTime,
		voiceCaptureErrorMessage,
		voiceDraftErrorMessage
	} from '$lib/scan/voice-capture';
	import { SCAN_HISTORY_FILTERS, formatScanHistoryEmptyMessage, resolveScanHistoryFilter, type ScanHistoryFilter } from '$lib/scans/scan-history-filters';

	const queryClient = useQueryClient();
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

	// Document type the scan should become: 'Expense' (receipt/bill) or
	// 'Payment' (rent check). Drives the LLM extraction + confirm flow.
	// Defaults to 'Expense' since receipts/bills are the most common capture.
	const initialScanContext = parseScanContext(page.url.searchParams);
	const scanContext = $derived(parseScanContext(page.url.searchParams));
	const DOC_TYPES = [
		{ value: 'Expense', label: 'Receipt / Bill', hint: 'Becomes an expense record' },
		{ value: 'Payment', label: 'Rent Check / Payment', hint: 'Becomes a payment record' },
		{ value: 'WorkOrder', label: 'Maintenance Request', hint: 'Becomes a work order' },
		{ value: 'Lease', label: 'Lease Agreement', hint: 'Becomes a lease record' },
		{ value: 'Application', label: 'Rental Application', hint: 'Becomes an applicant record' }
	] as const;
	let docType = $state<ScanDocType>(initialScanContext.type ?? 'Expense');
	const uploadCopy = $derived(scanUploadCopy(docType));
	let isPreparingUpload = $state(false);
	let isRecording = $state(false);
	let recordingSeconds = $state(0);
	let recorder: MediaRecorder | null = null;
	let voiceStream: MediaStream | null = null;
	let recordingTimer: ReturnType<typeof setInterval> | null = null;
	let voiceChunks: Blob[] = [];

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

	const uploadMutation = createMutation(() => ({
		mutationFn: (file: File) => scan.upload(file, docType),
		onSuccess: (res) => {
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			goto(appendScanContext(`/scan/${res.draftId}`, { ...scanContext, type: docType }));
		},
		onError: (err) => {
			toast.error(err instanceof Error ? err.message : 'Upload failed');
		}
	}));

	const voiceMutation = createMutation(() => ({
		mutationFn: ({ audio, mimeType }: { audio: Blob; mimeType: string }) =>
			scan.createVoiceDraft(audio, mimeType),
		onSuccess: (draft) => {
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			goto(appendScanContext(`/scan/${draft.id}`, { ...scanContext, type: docType }));
		},
		onError: (err) => {
			toast.error(voiceDraftErrorMessage(err));
		}
	}));

	async function handleFilesSelected(files: File[]) {
		try {
			isPreparingUpload = true;
			uploadMutation.mutate(await prepareScanDocumentUpload(files, { targetEntityType: docType }));
		} catch (err) {
			toast.error(err instanceof Error ? err.message : 'Could not prepare those files');
		} finally {
			isPreparingUpload = false;
		}
	}

	function clearRecordingTimer() {
		if (recordingTimer) {
			clearInterval(recordingTimer);
			recordingTimer = null;
		}
	}

	// Stop the mic, timer, and recorder WITHOUT submitting a draft. Used on unmount
	// so navigating away mid-recording never leaves the microphone live in the
	// background (the onstop handler is detached first so teardown can't fire the
	// upload as the page is torn down).
	function teardownVoiceCapture() {
		clearRecordingTimer();
		if (recorder) {
			recorder.ondataavailable = null;
			recorder.onstop = null;
			if (recorder.state !== 'inactive') {
				try {
					recorder.stop();
				} catch {
					// already inactive — nothing to stop
				}
			}
			recorder = null;
		}
		if (voiceStream) {
			voiceStream.getTracks().forEach((track) => track.stop());
			voiceStream = null;
		}
		isRecording = false;
		recordingSeconds = 0;
	}

	async function startVoiceCapture() {
		if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined') {
			// An insecure (non-https) origin hides mediaDevices entirely; say that's
			// why rather than implying the browser can never record.
			toast.error(
				typeof window !== 'undefined' && window.isSecureContext === false
					? 'Recording needs a secure (https) connection. Open the site over https and try again.'
					: 'Voice recording is not available in this browser.'
			);
			return;
		}

		try {
			const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
			voiceStream = stream;
			voiceChunks = [];
			recordingSeconds = 0;
			const nextRecorder = new MediaRecorder(stream);
			nextRecorder.ondataavailable = (event) => {
				if (event.data.size > 0) voiceChunks.push(event.data);
			};
			nextRecorder.onstop = () => {
				clearRecordingTimer();
				stream.getTracks().forEach((track) => track.stop());
				voiceStream = null;
				recorder = null;
				isRecording = false;
				const mimeType = nextRecorder.mimeType || 'audio/webm';
				const audio = new Blob(voiceChunks, { type: mimeType });
				if (audio.size === 0) {
					toast.error("Couldn't hear that — try again.");
					return;
				}
				if (audio.size > VOICE_MAX_AUDIO_BYTES) {
					toast.error('That recording is too large to send. Keep voice notes under about two minutes.');
					return;
				}
				voiceMutation.mutate({ audio, mimeType });
			};
			recorder = nextRecorder;
			nextRecorder.start();
			isRecording = true;
			// One interval drives both the elapsed-time display and the hard cap: at
			// the limit we auto-stop so a forgotten recording can't grow past Whisper's
			// size limit.
			recordingTimer = setInterval(() => {
				recordingSeconds += 1;
				if (recordingSeconds >= VOICE_MAX_RECORDING_SECONDS) {
					toast.info('Reached the two-minute limit — sending what we captured.');
					stopVoiceCapture();
				}
			}, 1000);
		} catch (err) {
			if (voiceStream) {
				voiceStream.getTracks().forEach((track) => track.stop());
				voiceStream = null;
			}
			toast.error(voiceCaptureErrorMessage(err));
		}
	}

	function stopVoiceCapture() {
		if (!recorder || recorder.state === 'inactive') return;
		clearRecordingTimer();
		isRecording = false;
		recorder.stop();
	}

	// Navigating away mid-recording must not leave the mic live: stop tracks + timer on unmount.
	$effect(() => {
		return () => teardownVoiceCapture();
	});

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
		if (type === 'Payment') return `/accounting/payments/${id}`;
		if (type === 'WorkOrder') return `/maintenance/${id}`;
		if (type === 'Lease') return `/leases/${id}`;
		if (type === 'Application') return `/applications/${id}`;
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
	{#if !uploadMutation.isPending && !isPreparingUpload}
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
		{#if uploadMutation.isPending || isPreparingUpload}
			<Card.Root class="flex items-center justify-center px-6 py-8 border-2 border-dashed">
				<Card.Content class="p-0">
					<div class="text-center">
						<div class="mx-auto mb-3 h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"></div>
						<p class="text-sm text-muted-foreground">{isPreparingUpload ? 'Preparing files…' : 'Uploading…'}</p>
					</div>
				</Card.Content>
			</Card.Root>
		{:else}
			<FileDrop multiple onselectedmany={handleFilesSelected} title={uploadCopy.title} helperText={uploadCopy.helperText} />
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
			<span class="text-sm text-muted-foreground tabular-nums" data-testid="voice-recording-status">
				Recording… {formatRecordingTime(recordingSeconds)} / {formatRecordingTime(VOICE_MAX_RECORDING_SECONDS)}
			</span>
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
