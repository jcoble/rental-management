<script lang="ts">
	import Dialog from '$lib/components/ui/Dialog.svelte';

	let {
		open = false,
		title = 'Are you sure?',
		message = 'This action cannot be undone.',
		confirmLabel = 'Delete',
		cancelLabel = 'Cancel',
		busy = false,
		testid = 'confirm',
		onconfirm,
		oncancel,
	}: {
		open?: boolean;
		title?: string;
		message?: string;
		confirmLabel?: string;
		cancelLabel?: string;
		busy?: boolean;
		testid?: string;
		onconfirm?: () => void;
		oncancel?: () => void;
	} = $props();
</script>

<Dialog {open} {title} onclose={oncancel}>
	<div data-testid="{testid}-dialog">
		<p class="text-sm text-muted-foreground">{message}</p>
		<div class="mt-4 flex justify-end gap-2">
			<button
				type="button"
				data-testid="{testid}-cancel"
				class="rounded-md border border-border px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
				onclick={oncancel}
				disabled={busy}
			>
				{cancelLabel}
			</button>
			<button
				type="button"
				data-testid="{testid}-confirm"
				class="rounded-md bg-destructive px-3 py-2 text-sm font-medium text-white transition-colors hover:bg-red-600 disabled:opacity-60"
				onclick={onconfirm}
				disabled={busy}
			>
				{busy ? 'Working…' : confirmLabel}
			</button>
		</div>
	</div>
</Dialog>
