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
		class="m3-state-layer inline-flex items-center justify-center rounded-[var(--m3-shape-full)] p-0.5 text-muted-foreground hover:text-foreground"
		aria-label={label ?? text}
		onmousedown={(e: MouseEvent) => {
			e.preventDefault();
			e.stopPropagation();
		}}
		onclick={(e: MouseEvent) => {
			e.preventDefault();
			e.stopPropagation();
		}}
	>
		<HelpCircle class={iconClass} />
	</Tooltip.Trigger>
	<Tooltip.Content {side}>{text}</Tooltip.Content>
</Tooltip.Root>
