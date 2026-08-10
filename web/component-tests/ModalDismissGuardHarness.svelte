<script lang="ts">
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Drawer from '$lib/components/ui/drawer';
	import NotificationHelpAction from '$lib/components/notifications/NotificationHelpAction.svelte';

	let dialogOpen = $state(false);
	let drawerOpen = $state(false);
	let readOnlyOpen = $state(false);
</script>

<div>
	<button type="button" data-testid="open-dialog" onclick={() => (dialogOpen = true)}>Open dialog</button>
	<button type="button" data-testid="open-sheet" onclick={() => (drawerOpen = true)}>Open sheet</button>
	<button type="button" data-testid="open-read-only" onclick={() => (readOnlyOpen = true)}>Open read-only dialog</button>
	<NotificationHelpAction
		title="Notification help"
		description="How notification settings work"
		guidance="Notifications are sent according to the settings you choose."
	/>
</div>

<Dialog.Root open={dialogOpen} onOpenChange={(next) => (dialogOpen = next)}>
	<Dialog.Content data-testid="data-entry-dialog">
		<Dialog.Title>Data entry dialog</Dialog.Title>
		<input data-testid="dialog-input" aria-label="Dialog value" />
		<button type="button" data-testid="dialog-cancel" onclick={() => (dialogOpen = false)}>Cancel</button>
	</Dialog.Content>
</Dialog.Root>

<Drawer.Root open={drawerOpen} onOpenChange={(next) => (drawerOpen = next)} direction="right" shouldScaleBackground={false}>
	<Drawer.Content data-testid="data-entry-sheet">
		<Drawer.Title>Data entry sheet</Drawer.Title>
		<input data-testid="sheet-input" aria-label="Sheet value" />
		<button type="button" data-testid="sheet-cancel" onclick={() => (drawerOpen = false)}>Cancel</button>
	</Drawer.Content>
</Drawer.Root>

<Dialog.Root open={readOnlyOpen} onOpenChange={(next) => (readOnlyOpen = next)}>
	<Dialog.Content data-testid="read-only-dialog">
		<Dialog.Title>Read-only dialog</Dialog.Title>
		<p>Nothing can be edited here.</p>
	</Dialog.Content>
</Dialog.Root>
