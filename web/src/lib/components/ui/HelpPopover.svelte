<script lang="ts">
	import { Popover, PopoverTrigger, PopoverContent } from '$lib/components/ui/popover';
	import { HelpCircle } from '@lucide/svelte';

	type Props = {
		title: string;
		summary: string;
		detail?: string;
		learnMoreUrl?: string;
		learnMoreLabel?: string;
		side?: 'top' | 'bottom' | 'left' | 'right';
		iconClass?: string;
	};

	let {
		title,
		summary,
		detail,
		learnMoreUrl,
		learnMoreLabel = 'Learn more',
		side = 'bottom',
		iconClass = 'h-3.5 w-3.5 text-muted-foreground'
	}: Props = $props();
</script>

<Popover>
	<PopoverTrigger
		class="inline-flex items-center justify-center rounded-full p-0.5 hover:bg-muted/50 transition-colors"
		aria-label="Help for {title}"
	>
		<HelpCircle class={iconClass} />
	</PopoverTrigger>
	<PopoverContent
		{side}
		class="w-72 p-3"
	>
		<div class="space-y-1.5">
			<p class="text-sm font-medium">{title}</p>
			<p class="text-xs text-muted-foreground leading-relaxed">{summary}</p>
			{#if detail}
				<p class="text-xs text-muted-foreground/80 leading-relaxed">{detail}</p>
			{/if}
			{#if learnMoreUrl}
				<a
					href={learnMoreUrl}
					class="inline-block text-xs text-primary hover:underline mt-1"
				>
					{learnMoreLabel} →
				</a>
			{/if}
		</div>
	</PopoverContent>
</Popover>
