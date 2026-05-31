<script lang="ts">
	import * as Tooltip from '$lib/components/ui/tooltip';

	type Props = {
		explanation: string;
		nextAction?: string;
		side?: 'top' | 'bottom' | 'left' | 'right';
		children: any;
	};

	let { explanation, nextAction, side = 'bottom', children }: Props = $props();
</script>

<Tooltip.Root>
	<Tooltip.Trigger class="inline-flex cursor-help">
		{@render children()}
	</Tooltip.Trigger>
	<Tooltip.Content {side} class="max-w-64">
		<p class="text-xs">{explanation}</p>
		{#if nextAction}
			<p class="text-xs text-muted-foreground mt-1">Next: {nextAction}</p>
		{/if}
	</Tooltip.Content>
</Tooltip.Root>
