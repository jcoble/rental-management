<script lang="ts">
	import type { Snippet } from 'svelte';

	let {
		label,
		id,
		hint,
		required = false,
		testId,
		children
	}: {
		label: string;
		id?: string;
		hint?: string;
		required?: boolean;
		testId?: string;
		children: Snippet;
	} = $props();
</script>

<div
	class="grid items-start gap-3 [grid-template-columns:repeat(auto-fit,minmax(min(100%,12rem),1fr))]"
	data-testid={testId}
>
	<div class="min-w-0" data-testid={testId ? `${testId}-label-group` : undefined}>
		<label for={id} class="block break-words text-sm text-muted-foreground" data-testid={testId ? `${testId}-label` : undefined}>
			{label}{#if required}<span class="ml-0.5 text-destructive">*</span>{/if}
		</label>
		{#if hint}
			<p class="mt-0.5 break-words text-xs leading-relaxed text-muted-foreground/70" data-testid={testId ? `${testId}-hint` : undefined}>{hint}</p>
		{/if}
	</div>
	<div class="min-w-0 w-full" data-testid={testId ? `${testId}-control` : undefined}>
		{@render children()}
	</div>
</div>
