<script lang="ts">
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';

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

<Dialog.Root
	{open}
	onOpenChange={(v) => { if (!v) oncancel?.(); }}
>
	<Dialog.Content data-testid="{testid}-dialog">
		<Dialog.Header>
			<Dialog.Title>{title}</Dialog.Title>
		</Dialog.Header>
		<p class="text-sm text-muted-foreground">{message}</p>
		<Dialog.Footer>
			<Button
				variant="outline"
				data-testid="{testid}-cancel"
				onclick={oncancel}
				disabled={busy}
			>
				{cancelLabel}
			</Button>
			<Button
				variant="destructive"
				data-testid="{testid}-confirm"
				onclick={onconfirm}
				disabled={busy}
			>
				{busy ? 'Working…' : confirmLabel}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
