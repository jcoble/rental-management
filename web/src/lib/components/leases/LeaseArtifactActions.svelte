<script lang="ts">
	import type { LegalDocumentArtifactSummary } from '$lib/types';
	import { Button } from '$lib/components/ui/button';
	import { Eye, FileDown } from '@lucide/svelte';

	let {
		issuedArtifact = null,
		executedArtifact = null,
		onview,
		ondownload,
		noExecutedArtifactMessage = null,
		testidPrefix = 'lease-artifact'
	}: {
		issuedArtifact?: LegalDocumentArtifactSummary | null;
		executedArtifact?: LegalDocumentArtifactSummary | null;
		onview: (artifact: LegalDocumentArtifactSummary) => void;
		ondownload: (artifact: LegalDocumentArtifactSummary) => void;
		noExecutedArtifactMessage?: string | null;
		testidPrefix?: string;
	} = $props();
</script>

<div class="flex flex-wrap items-center gap-2" data-testid={`${testidPrefix}-actions`}>
	{#if issuedArtifact}
		<Button
			variant="outline"
			size="sm"
			class="gap-2"
			onclick={() => onview(issuedArtifact)}
			data-testid={`${testidPrefix}-view-issued`}
		>
			<Eye class="h-4 w-4" /> View issued PDF
		</Button>
		<Button
			variant="outline"
			size="sm"
			class="gap-2"
			onclick={() => ondownload(issuedArtifact)}
			data-testid={`${testidPrefix}-download-issued`}
		>
			<FileDown class="h-4 w-4" /> Issued PDF
		</Button>
	{/if}
	{#if executedArtifact}
		<Button
			variant="outline"
			size="sm"
			class="gap-2"
			onclick={() => onview(executedArtifact)}
			data-testid={`${testidPrefix}-view-executed`}
		>
			<Eye class="h-4 w-4" /> View executed PDF
		</Button>
		<Button
			variant="outline"
			size="sm"
			class="gap-2"
			onclick={() => ondownload(executedArtifact)}
			data-testid={`${testidPrefix}-download-executed`}
		>
			<FileDown class="h-4 w-4" /> Executed PDF
		</Button>
	{:else if noExecutedArtifactMessage}
		<p class="text-sm text-muted-foreground" data-testid={`${testidPrefix}-no-executed`}>{noExecutedArtifactMessage}</p>
	{/if}
</div>
