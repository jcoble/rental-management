<script lang="ts">
	import { ExternalLink, HelpCircle, X } from '@lucide/svelte';

	let {
		title,
		description,
		guidance
	}: {
		title: string;
		description: string;
		guidance: string;
	} = $props();

	let dialog = $state<HTMLDialogElement>();
	let trigger = $state<HTMLButtonElement>();

	function openHelp() {
		dialog?.showModal();
	}

	function closeHelp() {
		dialog?.close();
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
>
	How this works <HelpCircle class="size-4" />
</button>

<dialog
	bind:this={dialog}
	class="m-auto w-[min(32rem,calc(100%-2rem))] rounded-xl border border-border bg-background p-0 text-foreground shadow-xl backdrop:bg-black/45"
	aria-labelledby="notification-help-title"
	aria-describedby="notification-help-description"
	onclose={returnFocus}
>
	<div class="space-y-4 p-6">
		<div class="flex items-start justify-between gap-4">
			<div>
				<h2 id="notification-help-title" class="text-lg font-semibold">{title}</h2>
				<p id="notification-help-description" class="mt-1 text-sm text-muted-foreground">{description}</p>
			</div>
			<button type="button" class="inline-flex size-11 shrink-0 items-center justify-center rounded-md hover:bg-muted" onclick={closeHelp} aria-label="Close help">
				<X class="size-5" />
			</button>
		</div>
		<p class="text-sm leading-6">{guidance}</p>
		<a href="/docs/settings-and-notifications" class="inline-flex min-h-11 items-center gap-2 text-sm font-medium text-primary hover:underline">
			Open notification documentation <ExternalLink class="size-4" />
		</a>
	</div>
</dialog>
