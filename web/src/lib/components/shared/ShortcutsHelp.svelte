<script lang="ts">
	import * as Dialog from '$lib/components/ui/dialog';
	import { shortcuts } from '$lib/shortcuts';

	let { open = $bindable(false) }: { open?: boolean } = $props();

	function formatKey(s: typeof shortcuts[0]): string {
		const parts: string[] = [];
		if (s.meta) parts.push('\u2318');
		if (s.shift) parts.push('\u21e7');
		parts.push(s.key.length === 1 ? s.key.toUpperCase() : s.key);
		return parts.join(' + ');
	}
</script>

<Dialog.Root {open} onOpenChange={(v) => { if (!v) open = false; }}>
	<Dialog.Content>
		<Dialog.Header>
			<Dialog.Title>Keyboard Shortcuts</Dialog.Title>
		</Dialog.Header>
		<div class="space-y-1">
			{#each shortcuts as shortcut}
				<div class="flex items-center justify-between py-1">
					<span class="text-sm text-muted-foreground">{shortcut.label}</span>
					<kbd class="rounded border border-border bg-secondary px-1.5 py-0.5 font-mono text-xs text-muted-foreground">
						{formatKey(shortcut)}
					</kbd>
				</div>
			{/each}
		</div>
	</Dialog.Content>
</Dialog.Root>
