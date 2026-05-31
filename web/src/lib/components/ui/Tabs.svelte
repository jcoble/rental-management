<script lang="ts">
	import { cn } from '$lib/utils/cn';

	let {
		tabs,
		active = '',
		onchange,
		class: className = '',
	}: {
		tabs: { value: string; label: string }[];
		active?: string;
		onchange?: (value: string) => void;
		class?: string;
	} = $props();
</script>

<div class={cn('flex gap-1 rounded-lg bg-secondary p-1', className)}>
	{#each tabs as tab}
		<button
			class="rounded-md px-3 py-1.5 text-xs font-medium transition-colors
				{active === tab.value
				? 'bg-card text-foreground shadow-sm'
				: 'text-muted-foreground hover:text-muted-foreground'}"
			onclick={() => onchange?.(tab.value)}
		>
			{tab.label}
		</button>
	{/each}
</div>
