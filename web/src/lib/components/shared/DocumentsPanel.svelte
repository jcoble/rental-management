<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { documents, downloadDocument } from '$lib/api/endpoints/documents';
	import type { DocumentItem } from '$lib/types';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import ConfirmDialog from './ConfirmDialog.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import { FileText, Image, Trash2, Upload, AlertCircle, FolderOpen } from '@lucide/svelte';
	import { ApiError } from '$lib/api/client';

	let {
		entityType,
		entityId,
		title = 'Documents',
		onChanged,
	}: {
		entityType: string;
		entityId: number;
		title?: string;
		onChanged?: () => void;
	} = $props();

	const queryClient = useQueryClient();

	const queryKey = $derived(['documents', entityType, entityId] as const);

	const docsQuery = createQuery(() => ({
		queryKey: queryKey,
		queryFn: () => documents.list(entityType, entityId),
		enabled: entityId > 0,
	}));

	const docs = $derived(docsQuery.data ?? []);

	// ── Upload ─────────────────────────────────────────────────────────────────
	let fileInput: HTMLInputElement | undefined = $state();
	let uploading = $state(false);
	let pendingUploadOperation = $state<{ fingerprint: string; id: string } | null>(null);

	function isTerminal(error: unknown): boolean {
		return error instanceof ApiError
			&& error.status >= 400 && error.status < 500
			&& error.status !== 408 && error.status !== 429;
	}

	async function handleFileChange(e: Event) {
		const input = e.currentTarget as HTMLInputElement;
		const file = input.files?.[0];
		if (!file) return;
		const fingerprint = `${entityType}:${entityId}:${file.name}:${file.type}:${file.size}:${file.lastModified}`;
		const operation = pendingUploadOperation?.fingerprint === fingerprint
			? pendingUploadOperation
			: { fingerprint, id: crypto.randomUUID() };
		pendingUploadOperation = operation;
		uploading = true;
		try {
			await documents.upload(entityType, entityId, file, undefined, operation.id);
			pendingUploadOperation = null;
			showSuccess(`"${file.name}" uploaded.`);
			queryClient.invalidateQueries({ queryKey: queryKey });
			onChanged?.();
		} catch (err) {
			if (isTerminal(err)) pendingUploadOperation = null;
			showError(apiErrorMessage(err, 'Upload failed.'));
		} finally {
			uploading = false;
			// Reset so the same file can be re-selected if needed.
			if (fileInput) fileInput.value = '';
		}
	}

	function triggerUpload() {
		fileInput?.click();
	}

	// ── Download ───────────────────────────────────────────────────────────────
	async function handleDownload(doc: DocumentItem) {
		try {
			await downloadDocument(doc.id, doc.fileName);
		} catch (err) {
			showError(apiErrorMessage(err, 'Download failed.'));
		}
	}

	// ── Delete ─────────────────────────────────────────────────────────────────
	let pendingDelete = $state<DocumentItem | null>(null);
	let deleteOperationId = $state<string | null>(null);

	const deleteMutation = createMutation(() => ({
		mutationFn: (id: number) => documents.delete(id, (deleteOperationId ??= crypto.randomUUID())),
		onSuccess: () => {
			showSuccess('Document deleted.');
			queryClient.invalidateQueries({ queryKey: queryKey });
			onChanged?.();
			pendingDelete = null;
			deleteOperationId = null;
		},
		onError: (err) => {
			showError(apiErrorMessage(err, 'Delete failed.'));
			if (isTerminal(err)) {
				pendingDelete = null;
				deleteOperationId = null;
			}
		},
	}));

	// ── Helpers ────────────────────────────────────────────────────────────────
	function formatSize(bytes?: number): string {
		if (bytes == null) return '';
		if (bytes < 1024) return `${bytes} B`;
		if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
		return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
	}

	function formatDate(iso: string): string {
		return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
	}
</script>

<Card.Root data-testid="documents-panel">
	<Card.Header class="flex flex-row items-center justify-between space-y-0 pb-3">
		<Card.Title class="text-base font-semibold">{title}</Card.Title>
		<Button
			size="sm"
			variant="outline"
			class="gap-1.5"
			onclick={triggerUpload}
			disabled={uploading}
			data-testid="documents-upload-btn"
		>
			<Upload class="h-3.5 w-3.5" />
			{uploading ? 'Uploading…' : 'Upload'}
		</Button>
		<!-- Hidden file input -->
		<input
			bind:this={fileInput}
			type="file"
			class="hidden"
			onchange={handleFileChange}
			data-testid="documents-file-input"
		/>
	</Card.Header>

	<Card.Content class="pt-0">
		{#if docsQuery.isLoading}
			<div class="flex items-center justify-center py-8 text-muted-foreground">
				<div class="flex flex-col items-center gap-2">
					<div class="h-5 w-5 animate-spin rounded-full border-2 border-current border-t-transparent"></div>
					<span class="text-xs">Loading…</span>
				</div>
			</div>

		{:else if docsQuery.isError}
			<div class="flex flex-col items-center gap-2 py-6 text-center text-destructive">
				<AlertCircle class="h-6 w-6" />
				<p class="text-sm">Could not load documents.</p>
				<Button size="sm" variant="outline" onclick={() => docsQuery.refetch()}>Retry</Button>
			</div>

		{:else if docs.length === 0}
			<div class="flex flex-col items-center gap-2 py-8 text-center text-muted-foreground" data-testid="documents-empty">
				<FolderOpen class="h-8 w-8 opacity-40" />
				<p class="text-sm">No documents yet.</p>
				<p class="text-xs opacity-70">Upload a file to attach it to this record.</p>
			</div>

		{:else}
			<ul class="divide-y divide-border" data-testid="documents-list">
				{#each docs as doc (doc.id)}
					<li class="flex items-center gap-3 py-2.5">
						<!-- File icon -->
						<div class="shrink-0 text-muted-foreground">
							{#if doc.isImage}
								<Image class="h-5 w-5" />
							{:else}
								<FileText class="h-5 w-5" />
							{/if}
						</div>

						<!-- Name + meta -->
						<div class="min-w-0 flex-1">
							<button
								type="button"
								class="block max-w-full truncate text-sm font-medium text-primary hover:underline focus:outline-none focus-visible:underline"
								onclick={() => handleDownload(doc)}
								data-testid="document-name-{doc.id}"
							>
								{doc.fileName}
							</button>
							<p class="text-xs text-muted-foreground">
								{#if doc.sizeBytes != null}{formatSize(doc.sizeBytes)} · {/if}{formatDate(doc.uploadedAt)}{doc.category ? ' · ' + doc.category : ''}
							</p>
						</div>

						<!-- Delete -->
						<Button
							size="icon"
							variant="ghost"
							class="h-7 w-7 shrink-0 text-muted-foreground hover:text-destructive"
							onclick={() => (pendingDelete = doc)}
							aria-label={`Delete ${doc.fileName}`}
							data-testid="document-delete-{doc.id}"
						>
							<Trash2 class="h-3.5 w-3.5" />
						</Button>
					</li>
				{/each}
			</ul>
		{/if}
	</Card.Content>
</Card.Root>

<ConfirmDialog
	open={pendingDelete !== null}
	title="Delete document"
	message={pendingDelete ? `Delete "${pendingDelete.fileName}"? This cannot be undone.` : ''}
	busy={deleteMutation.isPending}
	testid="documents-delete"
	onconfirm={() => pendingDelete && deleteMutation.mutate(pendingDelete.id)}
	oncancel={() => {
		pendingDelete = null;
		deleteOperationId = null;
	}}
/>
