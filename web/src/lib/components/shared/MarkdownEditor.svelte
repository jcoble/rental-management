<script lang="ts">
	import { marked } from 'marked';

	let {
		value = '',
		placeholder = 'Write something...',
		onchange,
	}: {
		value?: string;
		placeholder?: string;
		onchange?: (value: string) => void;
	} = $props();

	let mode = $state<'edit' | 'preview'>('edit');
	let rendered = $derived(marked.parse(value || '') as string);
</script>

<div class="rounded-lg border border-border">
	<!-- Toolbar -->
	<div class="flex items-center gap-1 border-b border-border px-2 py-1">
		<button
			onclick={() => (mode = 'edit')}
			class="rounded px-2 py-0.5 text-xs transition-colors {mode === 'edit' ? 'bg-secondary text-foreground' : 'text-muted-foreground hover:text-muted-foreground'}"
		>
			Edit
		</button>
		<button
			onclick={() => (mode = 'preview')}
			class="rounded px-2 py-0.5 text-xs transition-colors {mode === 'preview' ? 'bg-secondary text-foreground' : 'text-muted-foreground hover:text-muted-foreground'}"
		>
			Preview
		</button>
	</div>

	<!-- Content -->
	{#if mode === 'edit'}
		<textarea
			class="w-full resize-y bg-transparent px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground focus:outline-none"
			rows="4"
			{placeholder}
			bind:value
			oninput={() => onchange?.(value)}
		></textarea>
	{:else}
		<div class="prose prose-invert prose-sm max-w-none px-3 py-2 text-muted-foreground">
			{#if value}
				{@html rendered}
			{:else}
				<p class="text-muted-foreground italic">Nothing to preview</p>
			{/if}
		</div>
	{/if}
</div>
