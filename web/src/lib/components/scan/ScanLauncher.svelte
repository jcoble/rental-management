<script lang="ts">
	import { goto } from '$app/navigation';
	import { ScanLine } from '@lucide/svelte';

	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import { buildScanReviewTarget, contextualScanTitle, scanLauncherMode } from '$lib/scan/scan-launcher';
	import {
		SCAN_DOC_TYPES,
		type ScanContext,
		type ScanDocType,
		type ScanIntakeType
	} from '$lib/scan/scan-context';
	import ScanCapturePanel from './ScanCapturePanel.svelte';

	let {
		context = {},
		allowedTypes = SCAN_DOC_TYPES,
		allowVoice = true,
		open = $bindable(false),
		triggerLabel,
		ariaLabel,
		tooltip,
		testid = 'scan-launcher-trigger',
		triggerVariant = 'default',
		triggerClass = 'gap-2',
		showTrigger = true
	}: {
		context?: ScanContext;
		allowedTypes?: readonly ScanDocType[];
		allowVoice?: boolean;
		open?: boolean;
		triggerLabel?: string;
		ariaLabel?: string;
		tooltip?: string;
		testid?: string;
		triggerVariant?: 'default' | 'outline' | 'secondary' | 'ghost' | 'link' | 'destructive';
		triggerClass?: string;
		showTrigger?: boolean;
	} = $props();

	const title = $derived(contextualScanTitle(context));
	const mode = $derived(scanLauncherMode(context));
	const description = $derived(
		mode === 'contextual'
			? 'This scan will stay connected to the page you opened it from. After the document is read, review the fields and confirm the draft.'
			: 'Choose a document type, upload a PDF or photos, then review what the app extracted before it creates a record.'
	);

	function handleCreated(draftId: number, docType: ScanIntakeType) {
		open = false;
		goto(
			buildScanReviewTarget(
				draftId,
				docType === 'Auto' ? { ...context, type: undefined } : { ...context, type: docType }
			)
		);
	}
</script>

{#if showTrigger}
	<Button
		type="button"
		variant={triggerVariant}
		class={triggerClass}
		onclick={() => { open = true; }}
		aria-label={ariaLabel ?? title}
		data-m3-tooltip={tooltip}
		data-testid={testid}
	>
		<ScanLine class="h-4 w-4" />
		<span>{triggerLabel ?? 'Scan / Add'}</span>
	</Button>
{/if}

<Dialog.Root {open} onOpenChange={(value) => { open = value; }}>
	<Dialog.Content class="max-h-[88vh] max-w-3xl overflow-y-auto" data-testid="scan-launcher-dialog">
		<Dialog.Header>
			<Dialog.Title class="flex items-center gap-2">
				<ScanLine class="h-5 w-5 text-accent" />
				{title}
			</Dialog.Title>
			<Dialog.Description>{description}</Dialog.Description>
		</Dialog.Header>
		{#if mode === 'contextual' && context.sourceLabel}
			<div
				class="mt-4 rounded-lg border border-accent/30 bg-accent/5 px-4 py-3 text-sm"
				data-testid="scan-context-source"
			>
				<span class="text-muted-foreground">Connected to</span>
				<strong class="ml-1 font-semibold text-foreground">{context.sourceLabel}</strong>
			</div>
		{/if}
		<div class="mt-5">
			<ScanCapturePanel {context} {allowedTypes} {allowVoice} compact oncreated={handleCreated} />
		</div>
	</Dialog.Content>
</Dialog.Root>
