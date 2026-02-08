<script lang="ts">
	import { cn } from '$lib/utils/cn';

	let {
		value = 0,
		max = 100,
		class: className = '',
	}: {
		value?: number;
		max?: number;
		class?: string;
	} = $props();

	let pct = $derived(max > 0 ? Math.min(100, (value / max) * 100) : 0);
</script>

<div class={cn('h-2 w-full rounded-full bg-surface-hover', className)}>
	<div
		class="h-full rounded-full bg-accent transition-all duration-300"
		style="width: {pct}%"
		role="progressbar"
		aria-valuenow={value}
		aria-valuemin={0}
		aria-valuemax={max}
	></div>
</div>
