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
		confirmDisabled = false,
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
		confirmDisabled?: boolean;
		testid?: string;
		onconfirm?: () => void;
		oncancel?: () => void;
	} = $props();

	let confirming = $state(false);
	let wasBusy = $state(false);

	$effect(() => {
		if (!open) {
			confirming = false;
			wasBusy = false;
			return;
		}

		if (busy) {
			wasBusy = true;
		} else if (wasBusy) {
			confirming = false;
			wasBusy = false;
		}
	});

	function handleConfirm() {
		if (busy || confirming || confirmDisabled) return;
		confirming = true;
		onconfirm?.();
	}
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
				disabled={busy || confirming}
			>
				{cancelLabel}
			</Button>
			<Button
				variant="destructive"
				data-testid="{testid}-confirm"
				onclick={handleConfirm}
				disabled={busy || confirming || confirmDisabled}
			>
				{busy || confirming ? 'Working…' : confirmLabel}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
