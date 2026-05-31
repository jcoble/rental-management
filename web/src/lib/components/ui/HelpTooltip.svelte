<script lang="ts">
	import * as Tooltip from '$lib/components/ui/tooltip';
	import { HelpCircle } from '@lucide/svelte';

	type Props = {
		text: string;
		label?: string;
		side?: 'top' | 'bottom' | 'left' | 'right';
		iconClass?: string;
	};

	let {
		text,
		label,
		side = 'top',
		iconClass = 'h-3 w-3 text-muted-foreground'
	}: Props = $props();
</script>

<Tooltip.Root>
	<Tooltip.Trigger
		class="inline-flex"
		aria-label={label ?? text}
		title={text}
		onclick={(e: MouseEvent) => { e.preventDefault(); e.stopPropagation(); }}
	>
		<HelpCircle class={iconClass} />
	</Tooltip.Trigger>
	<Tooltip.Content {side}>{text}</Tooltip.Content>
</Tooltip.Root>
