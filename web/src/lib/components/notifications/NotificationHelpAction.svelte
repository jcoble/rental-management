<script lang="ts">
	import { ExternalLink, HelpCircle, X } from '@lucide/svelte';
	import * as Dialog from '$lib/components/ui/dialog';

	let {
		title,
		description,
		guidance,
		href = '/docs/settings-and-notifications',
		linkLabel = 'Open notification documentation'
	}: {
		title: string;
		description: string;
		guidance: string;
		href?: string;
		linkLabel?: string;
	} = $props();

	let open = $state(false);
	let trigger = $state<HTMLButtonElement>();

	function openHelp() {
		open = true;
	}

	function closeHelp() {
		open = false;
	}

	function returnFocus() {
		trigger?.focus();
	}
</script>

<button
	bind:this={trigger}
	type="button"
	class="inline-flex min-h-11 items-center gap-2 rounded-md px-3 text-sm font-medium text-primary hover:bg-primary/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
	onclick={openHelp}
	aria-haspopup="dialog"
	data-testid="notification-help-trigger"
>
	How this works <HelpCircle class="size-4" />
</button>

<Dialog.Root
	{open}
	onOpenChange={(next) => {
		open = next;
		if (!next) returnFocus();
	}}
>
	<Dialog.Content class="w-[min(32rem,calc(100%-2rem))]" showCloseButton={false} data-testid="notification-help-dialog">
		<div class="space-y-4">
			<div class="flex items-start justify-between gap-4">
				<div>
					<Dialog.Title class="text-lg font-semibold">{title}</Dialog.Title>
					<Dialog.Description class="mt-1 text-sm text-muted-foreground">{description}</Dialog.Description>
				</div>
				<button
					type="button"
					class="inline-flex size-11 shrink-0 items-center justify-center rounded-md hover:bg-muted"
					onclick={closeHelp}
					aria-label="Close help"
					data-testid="notification-help-close"
				>
					<X class="size-5" />
				</button>
			</div>
			<p class="text-sm leading-6">{guidance}</p>
			<a {href} class="inline-flex min-h-11 items-center gap-2 text-sm font-medium text-primary hover:underline" data-testid="notification-help-link">
				{linkLabel} <ExternalLink class="size-4" />
			</a>
		</div>
	</Dialog.Content>
</Dialog.Root>
