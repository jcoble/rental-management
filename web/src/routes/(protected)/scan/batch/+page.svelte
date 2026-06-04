<script lang="ts">
	import { createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { toast } from 'svelte-sonner';
	import { scan } from '$lib/api/scan';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import { FileText, Upload, X } from '@lucide/svelte';

	const queryClient = useQueryClient();

	const MAX_FILES = 100;

	let selectedFiles = $state<File[]>([]);
	let isDragging = $state(false);
	let inputEl = $state<HTMLInputElement | null>(null);

	function formatFileSize(bytes: number): string {
		if (bytes < 1024) return `${bytes} B`;
		if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
		return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
	}

	function isPdf(file: File): boolean {
		return file.type === 'application/pdf' || file.name.toLowerCase().endsWith('.pdf');
	}

	function addFiles(incoming: FileList | File[]) {
		const list = Array.from(incoming);
		const pdfs = list.filter(isPdf);
		const skipped = list.length - pdfs.length;
		if (skipped > 0) {
			toast.warning(
				`${skipped} file${skipped === 1 ? '' : 's'} skipped — only PDF lease documents are accepted.`
			);
		}
		// De-dupe by name + size so re-selecting the same files doesn't pile up.
		const existing = new Set(selectedFiles.map((f) => `${f.name}:${f.size}`));
		const next = [...selectedFiles];
		for (const f of pdfs) {
			const key = `${f.name}:${f.size}`;
			if (!existing.has(key)) {
				existing.add(key);
				next.push(f);
			}
		}
		if (next.length > MAX_FILES) {
			toast.warning(`You can import up to ${MAX_FILES} leases at a time. Extra files were dropped.`);
			next.length = MAX_FILES;
		}
		selectedFiles = next;
	}

	function removeFile(index: number) {
		selectedFiles = selectedFiles.filter((_, i) => i !== index);
	}

	function clearAll() {
		selectedFiles = [];
		if (inputEl) inputEl.value = '';
	}

	function onDragOver(e: DragEvent) {
		e.preventDefault();
		isDragging = true;
	}
	function onDragLeave(e: DragEvent) {
		e.preventDefault();
		isDragging = false;
	}
	function onDrop(e: DragEvent) {
		e.preventDefault();
		isDragging = false;
		if (e.dataTransfer?.files?.length) addFiles(e.dataTransfer.files);
	}
	function onClick() {
		inputEl?.click();
	}
	function onInputChange(e: Event) {
		const files = (e.target as HTMLInputElement).files;
		if (files?.length) addFiles(files);
	}

	const uploadMutation = createMutation(() => ({
		mutationFn: (files: File[]) => scan.uploadBatch(files, { targetEntityType: 'Lease' }),
		onSuccess: (res) => {
			queryClient.invalidateQueries({ queryKey: ['scan-batches'] });
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			toast.success(
				`Importing ${res.fileCount} lease${res.fileCount === 1 ? '' : 's'} — we'll pull out the details for you.`
			);
			goto(`/scan/batch/${res.batchId}`);
		},
		onError: (err) => {
			toast.error(err instanceof Error ? err.message : 'Import failed');
		}
	}));

	function startImport() {
		if (selectedFiles.length === 0) return;
		uploadMutation.mutate(selectedFiles);
	}
</script>

<svelte:head>
	<title>Import leases - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="scan-batch-import-page">
	<div class="mb-4">
		<PageBreadcrumb
			crumbs={[
				{ label: 'Scan', href: '/scan' },
				{ label: 'Import leases' }
			]}
		/>
	</div>

	<div class="mb-6">
		<h1 class="text-2xl font-bold">Import existing leases</h1>
		<p class="text-sm text-muted-foreground">
			Have a stack of lease PDFs? Drop them all in at once. The computer reads each one and pulls
			out the tenant, unit, and term — you just confirm.
		</p>
	</div>

	<!-- Drop zone -->
	{#if !uploadMutation.isPending}
		<div
			data-testid="batch-file-drop"
			role="button"
			tabindex="0"
			class="relative mb-4 flex cursor-pointer flex-col items-center justify-center rounded-lg border-2 border-dashed px-6 py-10 transition-colors
				{isDragging
				? 'border-accent bg-primary/5'
				: 'border-border bg-card hover:border-accent/60 hover:bg-secondary'}"
			ondragover={onDragOver}
			ondragleave={onDragLeave}
			ondrop={onDrop}
			onclick={onClick}
			onkeydown={(e) => (e.key === 'Enter' || e.key === ' ' ? onClick() : null)}
		>
			<input
				bind:this={inputEl}
				data-testid="batch-file-input"
				type="file"
				accept="application/pdf"
				multiple
				class="sr-only hidden"
				onchange={onInputChange}
			/>
			<Upload class="mb-3 h-10 w-10 text-muted-foreground" />
			<p class="text-sm font-medium text-foreground">Drop lease PDFs here</p>
			<p class="mt-1 text-xs text-muted-foreground">
				or click to browse — up to {MAX_FILES} PDFs at a time
			</p>
		</div>
	{:else}
		<Card.Root class="mb-4 flex items-center justify-center border-2 border-dashed px-6 py-10">
			<Card.Content class="p-0">
				<div class="text-center">
					<div
						class="mx-auto mb-3 h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"
					></div>
					<p class="text-sm text-muted-foreground">
						Uploading {selectedFiles.length} lease{selectedFiles.length === 1 ? '' : 's'}…
					</p>
				</div>
			</Card.Content>
		</Card.Root>
	{/if}

	<!-- Selected files list -->
	{#if selectedFiles.length > 0}
		<Card.Root class="mb-4" data-testid="batch-selected-files">
			<Card.Header class="flex flex-row items-center justify-between gap-2 space-y-0">
				<Card.Title class="text-base">
					{selectedFiles.length} lease{selectedFiles.length === 1 ? '' : 's'} ready to import
				</Card.Title>
				{#if !uploadMutation.isPending}
					<Button
						variant="ghost"
						size="sm"
						class="h-auto p-1 text-muted-foreground"
						data-testid="batch-clear-all"
						onclick={clearAll}
					>
						Clear all
					</Button>
				{/if}
			</Card.Header>
			<Card.Content>
				<ul class="divide-y divide-border">
					{#each selectedFiles as file, i (file.name + file.size)}
						<li
							class="flex items-center gap-3 py-2 text-sm"
							data-testid="batch-file-row"
						>
							<FileText class="h-5 w-5 shrink-0 text-primary" />
							<div class="min-w-0 flex-1">
								<p class="truncate font-medium text-foreground">{file.name}</p>
								<p class="text-xs text-muted-foreground">{formatFileSize(file.size)}</p>
							</div>
							{#if !uploadMutation.isPending}
								<button
									type="button"
									class="rounded p-1 text-muted-foreground hover:bg-secondary hover:text-destructive"
									aria-label="Remove {file.name}"
									data-testid="batch-file-remove-{i}"
									onclick={() => removeFile(i)}
								>
									<X class="h-4 w-4" />
								</button>
							{/if}
						</li>
					{/each}
				</ul>
			</Card.Content>
		</Card.Root>
	{/if}

	<!-- Actions -->
	<div class="flex items-center gap-2">
		<Button
			data-testid="batch-import-button"
			disabled={selectedFiles.length === 0 || uploadMutation.isPending}
			onclick={startImport}
		>
			{#if uploadMutation.isPending}
				Importing…
			{:else}
				Import {selectedFiles.length} lease{selectedFiles.length === 1 ? '' : 's'}
			{/if}
		</Button>
		<Button variant="outline" href="/scan" data-testid="batch-cancel">Cancel</Button>
	</div>
</div>
