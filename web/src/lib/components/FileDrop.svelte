<script lang="ts">
	import { toast } from 'svelte-sonner';

	const ALLOWED_MIME_TYPES = [
		'application/pdf',
		'image/jpeg',
		'image/jpg',
		'image/png',
		'image/gif',
		'image/webp',
		'image/heic',
		'image/heif',
		'image/bmp',
		'image/tiff'
	];

	let { onselected }: { onselected: (file: File) => void } = $props();

	let isDragging = $state(false);
	let selectedFile = $state<File | null>(null);
	let inputEl = $state<HTMLInputElement | null>(null);

	function formatFileSize(bytes: number): string {
		if (bytes < 1024) return `${bytes} B`;
		if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
		return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
	}

	function handleFile(file: File) {
		if (!ALLOWED_MIME_TYPES.includes(file.type)) {
			toast.warning(`File type "${file.type || 'unknown'}" may not be supported. Expected a PDF or image.`);
		}
		selectedFile = file;
		onselected(file);
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
		const file = e.dataTransfer?.files?.[0];
		if (file) handleFile(file);
	}

	function onClick() {
		inputEl?.click();
	}

	function onInputChange(e: Event) {
		const file = (e.target as HTMLInputElement).files?.[0];
		if (file) handleFile(file);
	}

	function clearFile(e: MouseEvent) {
		e.stopPropagation();
		selectedFile = null;
		if (inputEl) inputEl.value = '';
	}
</script>

<div
	data-testid="file-drop"
	role="button"
	tabindex="0"
	class="relative flex cursor-pointer flex-col items-center justify-center rounded-lg border-2 border-dashed px-6 py-8 transition-colors
		{isDragging
		? 'border-accent bg-primary/5'
		: 'border-border bg-card hover:border-accent/60 hover:bg-secondary'}"
	ondragover={onDragOver}
	ondragleave={onDragLeave}
	ondrop={onDrop}
	onclick={onClick}
	onkeydown={(e) => e.key === 'Enter' || e.key === ' ' ? onClick() : null}
>
	<input
		bind:this={inputEl}
		data-testid="file-drop-input"
		type="file"
		accept="application/pdf,image/*"
		capture="environment"
		class="sr-only hidden"
		onchange={onInputChange}
	/>

	{#if selectedFile}
		<div class="flex items-center gap-3 text-sm">
			<svg class="h-8 w-8 flex-shrink-0 text-primary" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="1.5">
				<path stroke-linecap="round" stroke-linejoin="round" d="M19.5 14.25v-2.625a3.375 3.375 0 00-3.375-3.375h-1.5A1.125 1.125 0 0113.5 7.125v-1.5a3.375 3.375 0 00-3.375-3.375H8.25m0 12.75h7.5m-7.5 3H12M10.5 2.25H5.625c-.621 0-1.125.504-1.125 1.125v17.25c0 .621.504 1.125 1.125 1.125h12.75c.621 0 1.125-.504 1.125-1.125V11.25a9 9 0 00-9-9z" />
			</svg>
			<div class="min-w-0 flex-1">
				<p class="truncate font-medium text-foreground">{selectedFile.name}</p>
				<p class="text-muted-foreground">{formatFileSize(selectedFile.size)}</p>
			</div>
			<button
				type="button"
				class="rounded p-1 text-muted-foreground hover:bg-secondary hover:text-destructive"
				onclick={clearFile}
				aria-label="Clear selected file"
			>
				<svg class="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
					<path stroke-linecap="round" stroke-linejoin="round" d="M6 18L18 6M6 6l12 12" />
				</svg>
			</button>
		</div>
	{:else}
		<svg class="mb-3 h-10 w-10 text-muted-foreground" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="1.5">
			<path stroke-linecap="round" stroke-linejoin="round" d="M3 16.5v2.25A2.25 2.25 0 005.25 21h13.5A2.25 2.25 0 0021 18.75V16.5m-13.5-9L12 3m0 0l4.5 4.5M12 3v13.5" />
		</svg>
		<p class="text-sm font-medium text-foreground">Drop a receipt or invoice here</p>
		<p class="mt-1 text-xs text-muted-foreground">or click to browse — PDF, JPG, PNG accepted</p>
	{/if}
</div>
