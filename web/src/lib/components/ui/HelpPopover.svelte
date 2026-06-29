<script lang="ts">
	import * as Tooltip from "$lib/components/ui/tooltip";
	import { HelpCircle } from "@lucide/svelte";

	type Props = {
		title: string;
		summary: string;
		detail?: string;
		learnMoreUrl?: string;
		learnMoreLabel?: string;
		side?: "top" | "bottom" | "left" | "right";
		iconClass?: string;
		testid?: string;
	};

	let {
		title,
		summary,
		detail,
		learnMoreUrl,
		learnMoreLabel = "Learn more",
		side = "bottom",
		iconClass = "h-3.5 w-3.5 text-muted-foreground",
		testid,
	}: Props = $props();

	function stopClickOpen(event: MouseEvent) {
		event.preventDefault();
		event.stopPropagation();
	}
</script>

<Tooltip.Root delayDuration={250}>
	<Tooltip.Trigger
		class="m3-state-layer inline-flex items-center justify-center rounded-[var(--m3-shape-full)] p-0.5 text-muted-foreground hover:text-foreground"
		aria-label="Help for {title}"
		data-testid={testid}
		onmousedown={stopClickOpen}
		onclick={stopClickOpen}
	>
		<HelpCircle class={iconClass} />
	</Tooltip.Trigger>
	<Tooltip.Content {side} variant="rich" class="w-72">
		<div class="space-y-1.5">
			<p class="text-sm font-semibold">{title}</p>
			<p class="text-xs leading-relaxed opacity-85">{summary}</p>
			{#if detail}
				<p class="text-xs leading-relaxed opacity-75">{detail}</p>
			{/if}
			{#if learnMoreUrl}
				<a href={learnMoreUrl} class="mt-1 inline-block text-xs font-semibold underline-offset-4 hover:underline">
					{learnMoreLabel}
				</a>
			{/if}
		</div>
	</Tooltip.Content>
</Tooltip.Root>
