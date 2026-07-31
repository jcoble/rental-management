<script lang="ts">
	import { goto } from '$app/navigation';
	import { createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { Mic, Square } from '@lucide/svelte';
	import { toast } from 'svelte-sonner';

	import { scan } from '$lib/api/scan';
	import FileDrop from '$lib/components/FileDrop.svelte';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import { buildScanReviewTarget, shouldAskForScanDocumentType } from '$lib/scan/scan-launcher';
	import { scanUploadCopy } from '$lib/scan/scan-copy';
	import { prepareScanDocumentUpload } from '$lib/scan/scan-upload';
	import {
		SCAN_DOC_TYPES,
		type ScanContext,
		type ScanDocType,
		type ScanIntakeType
	} from '$lib/scan/scan-context';
	import {
		VOICE_MAX_AUDIO_BYTES,
		VOICE_MAX_RECORDING_SECONDS,
		formatRecordingTime,
		voiceCaptureErrorMessage,
		voiceDraftErrorMessage
	} from '$lib/scan/voice-capture';

	const queryClient = useQueryClient();

	const AUTO_TYPE: { value: ScanIntakeType; label: string; hint: string } = {
		value: 'Auto',
		label: 'Auto / classify for me',
		hint: 'Best for mixed paperwork — identify it first, then show the right review'
	};

	const DOC_TYPES: { value: ScanDocType; label: string; hint: string }[] = [
		{ value: 'Expense', label: 'Receipt / Bill', hint: 'Becomes an expense record' },
		{ value: 'Payment', label: 'Rent Check / Payment', hint: 'Becomes a payment record' },
		{ value: 'WorkOrder', label: 'Maintenance Request', hint: 'Becomes a work order' },
		{ value: 'LeaseAgreement', label: 'Lease Agreement', hint: 'Becomes an agreement record' },
		{ value: 'Application', label: 'Rental Application', hint: 'Becomes an applicant record' },
		{ value: 'Loan', label: 'Mortgage / Loan', hint: 'Becomes a loan on the property' },
		{
			value: 'LeaseEndingNotice',
			label: 'Move-out Notice',
			hint: 'Attaches notice and starts move-out'
		}
	];

	let {
		context = {},
		defaultType = context.type ?? 'Expense',
		allowedTypes = SCAN_DOC_TYPES,
		allowVoice = true,
		compact = false,
		oncreated
	}: {
		context?: ScanContext;
		defaultType?: ScanDocType;
		allowedTypes?: readonly ScanDocType[];
		allowVoice?: boolean;
		compact?: boolean;
		oncreated?: (draftId: number, docType: ScanIntakeType) => void;
	} = $props();

	let docType = $state<ScanIntakeType>('Expense');
	let lastDefaultType = $state<ScanIntakeType | null>(null);
	let isPreparingUpload = $state(false);
	let isRecording = $state(false);
	let recordingSeconds = $state(0);
	let recorder: MediaRecorder | null = null;
	let voiceStream: MediaStream | null = null;
	let recordingTimer: ReturnType<typeof setInterval> | null = null;
	let voiceChunks: Blob[] = [];

	const availableDocTypes = $derived(DOC_TYPES.filter((option) => allowedTypes.includes(option.value)));
	const canAutoClassify = $derived(
		SCAN_DOC_TYPES.every((type) => allowedTypes.includes(type)) && !context.type
	);
	const availableIntakeTypes = $derived([
		...(canAutoClassify ? [AUTO_TYPE] : []),
		...availableDocTypes
	]);
	const askForType = $derived(shouldAskForScanDocumentType(context) && availableDocTypes.length > 1);
	const uploadCopy = $derived(scanUploadCopy(docType));
	const selectedTarget = $derived(docType === 'Auto' ? undefined : docType);

	$effect(() => {
		const requestedDefault: ScanIntakeType = context.type ?? (canAutoClassify ? 'Auto' : defaultType);
		const nextDefault = requestedDefault === 'Auto' || allowedTypes.includes(requestedDefault)
			? requestedDefault
			: availableDocTypes[0]?.value;
		if (!nextDefault) return;
		if (lastDefaultType !== nextDefault) {
			lastDefaultType = nextDefault;
			docType = nextDefault;
		}
	});

	function navigateToDraft(draftId: number) {
		oncreated?.(draftId, docType);
		if (!oncreated) {
			goto(
				buildScanReviewTarget(
					draftId,
					docType === 'Auto' ? { ...context, type: undefined } : { ...context, type: docType }
				)
			);
		}
	}

	const uploadMutation = createMutation(() => ({
		mutationFn: (file: File) => scan.upload(file, selectedTarget, context),
		onSuccess: (res) => {
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			navigateToDraft(res.draftId);
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
			navigateToDraft(draft.id);
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

	function teardownVoiceCapture() {
		clearRecordingTimer();
		if (recorder) {
			recorder.ondataavailable = null;
			recorder.onstop = null;
			if (recorder.state !== 'inactive') {
				try {
					recorder.stop();
				} catch {
					// Already inactive.
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
			toast.error(
				typeof window !== 'undefined' && window.isSecureContext === false
					? 'Recording needs a secure https connection. Open the site over https and try again.'
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
					toast.error("Couldn't hear that. Try again.");
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
			recordingTimer = setInterval(() => {
				recordingSeconds += 1;
				if (recordingSeconds >= VOICE_MAX_RECORDING_SECONDS) {
					toast.info('Reached the two-minute limit. Sending what we captured.');
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

	$effect(() => {
		return () => teardownVoiceCapture();
	});
</script>

<div class={compact ? 'space-y-4' : 'space-y-6'} data-testid="scan-capture-panel">
	{#if availableDocTypes.length === 0}
		<div class="rounded-lg border border-border bg-muted/30 p-4" data-testid="scan-no-authorized-types">
			<p class="text-sm font-medium">No scan command is available for this access.</p>
			<p class="mt-1 text-xs text-muted-foreground">Switch to an authorized workspace experience or ask an administrator to update your assignment.</p>
		</div>
	{:else if askForType && !uploadMutation.isPending && !isPreparingUpload}
		<div data-testid="scan-doc-type">
			<div class="mb-2 flex items-center gap-1.5">
				<span class="block text-sm font-medium">What are you scanning?</span>
				<HelpPopover
					title="Pick the kind of document"
					summary="Choose the record this document should become so the extraction uses the right fields."
					detail="Contextual scans skip this because the page already knows whether you are scanning a lease, payment, expense, or work order."
					learnMoreUrl={undefined}
				/>
			</div>
			<div class="grid gap-3 sm:grid-cols-2 {compact ? '' : 'lg:grid-cols-3'}">
				{#each availableIntakeTypes as opt}
					<button
						type="button"
						data-testid="scan-doc-type-{opt.value}"
						aria-pressed={docType === opt.value}
						onclick={() => { docType = opt.value; }}
						class="flex flex-col items-start gap-0.5 rounded-lg border px-4 py-3 text-left transition-[color,background-color,border-color,transform] active:scale-[0.98] {docType === opt.value
							? 'border-accent bg-accent/10 ring-2 ring-accent'
							: 'border-border bg-background hover:bg-muted/50'}"
					>
						<span class="flex items-center gap-2 text-sm font-semibold">
							{opt.label}
							{#if opt.value === 'Auto'}
								<span class="rounded-full border border-accent/40 bg-accent/10 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-accent">Recommended</span>
							{/if}
						</span>
						<span class="text-xs text-muted-foreground">{opt.hint}</span>
					</button>
				{/each}
			</div>
		</div>
	{/if}

	{#if availableDocTypes.length > 0}<div data-testid="scan-upload">
		{#if uploadMutation.isPending || isPreparingUpload}
			<Card.Root class="flex items-center justify-center border-2 border-dashed px-6 py-8">
				<Card.Content class="p-0">
					<div class="text-center">
						<div class="mx-auto mb-3 h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"></div>
						<p class="text-sm text-muted-foreground">{isPreparingUpload ? 'Preparing files...' : 'Uploading...'}</p>
					</div>
				</Card.Content>
			</Card.Root>
		{:else}
			<FileDrop multiple onselectedmany={handleFilesSelected} title={uploadCopy.title} helperText={uploadCopy.helperText} />
		{/if}
	</div>{/if}

	{#if allowVoice && availableDocTypes.length > 0}<div class="flex flex-wrap items-center gap-3 border-t pt-4" data-testid="voice-capture">
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
			summary="Speak instead of type. Describe an expense, payment, or repair and the app creates a draft for review."
			detail="Voice capture is useful when you are on a phone or have a paper note but do not want to fill out a form yet."
			learnMoreUrl={undefined}
		/>
		{#if voiceMutation.isPending}
			<span class="text-sm text-muted-foreground">Creating draft...</span>
		{:else if isRecording}
			<span class="text-sm text-muted-foreground tabular-nums" data-testid="voice-recording-status">
				Recording {formatRecordingTime(recordingSeconds)} / {formatRecordingTime(VOICE_MAX_RECORDING_SECONDS)}
			</span>
		{/if}
	</div>{/if}
</div>
