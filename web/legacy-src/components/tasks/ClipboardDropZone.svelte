<script lang="ts">
	import type { Attachment } from '$lib/types';
	import { pasteScreenshotToTask } from '$lib/utils/clipboard';
	import { Upload, ClipboardPaste } from '@lucide/svelte';

	let { taskId, onUpload }: { taskId: number; onUpload: (attachment: Attachment) => void } = $props();
	let isDragging = $state(false);
	let pasting = $state(false);

	function handlePaste(e: ClipboardEvent) {
		const items = e.clipboardData?.items;
		if (!items) return;
		for (const item of items) {
			if (item.type.startsWith('image/')) {
				const file = item.getAsFile();
				if (file) uploadFile(file);
			}
		}
	}

	function handleDrop(e: DragEvent) {
		e.preventDefault();
		isDragging = false;
		const files = e.dataTransfer?.files;
		if (files) {
			for (const file of files) uploadFile(file);
		}
	}

	function handleDragOver(e: DragEvent) {
		e.preventDefault();
		isDragging = true;
	}

	async function handleClickPaste() {
		pasting = true;
		const attachment = await pasteScreenshotToTask(taskId);
		if (attachment) onUpload(attachment);
		pasting = false;
	}

	async function uploadFile(file: File) {
		const formData = new FormData();
		formData.append('file', file);
		const res = await fetch(`/api/tasks/${taskId}/attachments`, { method: 'POST', body: formData });
		const attachment = await res.json();
		onUpload(attachment);
	}
</script>

<svelte:window onpaste={handlePaste} />

<div class="flex gap-2">
	<div
		class="flex-1 border-2 border-dashed rounded-lg p-4 text-center transition-colors cursor-pointer
			{isDragging ? 'border-accent bg-accent/10' : 'border-border text-text-tertiary hover:border-border-hover'}"
		role="button"
		tabindex="0"
		ondragover={handleDragOver}
		ondragleave={() => (isDragging = false)}
		ondrop={handleDrop}
	>
		<Upload class="mx-auto mb-1 h-4 w-4" />
		<p class="text-xs">Paste screenshot or drop files here</p>
	</div>
	<button
		onclick={handleClickPaste}
		disabled={pasting}
		class="flex flex-col items-center justify-center rounded-lg border-2 border-dashed border-border px-3 text-text-tertiary transition-colors hover:border-accent hover:text-accent disabled:opacity-50"
		title="Paste from clipboard"
	>
		<ClipboardPaste class="h-4 w-4" />
		<span class="mt-0.5 text-[10px]">{pasting ? 'Pasting...' : 'Paste'}</span>
	</button>
</div>
