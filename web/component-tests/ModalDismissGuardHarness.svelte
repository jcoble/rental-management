<script lang="ts">
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Drawer from '$lib/components/ui/drawer';
	import * as Command from '$lib/components/ui/command';
	import NotificationHelpAction from '$lib/components/notifications/NotificationHelpAction.svelte';
	import UnitTimelineRail from '$lib/components/unit/UnitTimelineRail.svelte';
	import type { AuditEntry } from '$lib/types';

	let dialogOpen = $state(false);
	let drawerOpen = $state(false);
	let readOnlyOpen = $state(false);
	let transientOpen = $state(false);
	let commandOpen = $state(false);
	const activities: AuditEntry[] = [
		{
			id: 814,
			portfolioId: 1,
			operation: 'Updated',
			operationName: 'Updated',
			entityType: 'Unit',
			entityId: 814,
			actor: 'Test landlord',
			description: 'Unit details updated',
			timestamp: '2026-08-10T12:00:00.000Z',
			testId: 'timeline-activity',
			changes: [{ field: 'Status', oldValue: 'Vacant', newValue: 'Occupied' }]
		}
	];
</script>

<div>
	<button type="button" data-testid="open-dialog" onclick={() => (dialogOpen = true)}>Open dialog</button>
	<button type="button" data-testid="open-sheet" onclick={() => (drawerOpen = true)}>Open sheet</button>
	<button type="button" data-testid="open-read-only" onclick={() => (readOnlyOpen = true)}>Open read-only dialog</button>
	<button type="button" data-testid="open-transient" onclick={() => (transientOpen = true)}>Open transient controls</button>
	<button type="button" data-testid="open-command" onclick={() => (commandOpen = true)}>Open command palette</button>
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
		<button type="button" data-testid="sheet-close" onclick={() => (drawerOpen = false)}>Close</button>
	</Drawer.Content>
</Drawer.Root>

<Dialog.Root open={readOnlyOpen} onOpenChange={(next) => (readOnlyOpen = next)}>
	<Dialog.Content data-testid="read-only-dialog">
		<Dialog.Title>Read-only dialog</Dialog.Title>
		<p>Nothing can be edited here.</p>
	</Dialog.Content>
</Dialog.Root>

<Dialog.Root open={transientOpen} onOpenChange={(next) => (transientOpen = next)}>
	<Dialog.Content data-testid="transient-dialog">
		<Dialog.Title>Transient controls</Dialog.Title>
		<input data-testid="command-search-control" data-slot="command-input" aria-label="Search" />
		<button type="button" data-testid="disclosure-control" aria-expanded="false">Details</button>
		<button type="button" data-testid="popover-control" aria-haspopup="dialog">More information</button>
		<button type="button" data-testid="menu-control" aria-haspopup="menu" data-state="closed">Actions</button>
		<button type="button" data-testid="tooltip-control" data-slot="tooltip-trigger">Help</button>
	</Dialog.Content>
</Dialog.Root>

<Command.Dialog open={commandOpen} onOpenChange={(next) => (commandOpen = next)}>
	<Command.Input data-testid="command-dialog-input" placeholder="Search commands" />
	<Command.List>
		<Command.Item value="Open settings">Open settings</Command.Item>
	</Command.List>
</Command.Dialog>

<UnitTimelineRail activities={activities} onViewAll={() => undefined} />
