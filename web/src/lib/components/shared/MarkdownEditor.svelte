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
			class="rounded px-2 py-0.5 text-xs transition-colors {mode === 'edit' ? 'bg-surface-hover text-text-primary' : 'text-text-tertiary hover:text-text-secondary'}"
		>
			Edit
		</button>
		<button
			onclick={() => (mode = 'preview')}
			class="rounded px-2 py-0.5 text-xs transition-colors {mode === 'preview' ? 'bg-surface-hover text-text-primary' : 'text-text-tertiary hover:text-text-secondary'}"
		>
			Preview
		</button>
	</div>

	<!-- Content -->
	{#if mode === 'edit'}
		<textarea
			class="w-full resize-y bg-transparent px-3 py-2 text-sm text-text-primary placeholder:text-text-tertiary focus:outline-none"
			rows="4"
			{placeholder}
			bind:value
			oninput={() => onchange?.(value)}
		></textarea>
	{:else}
		<div class="prose prose-invert prose-sm max-w-none px-3 py-2 text-text-secondary">
			{#if value}
				{@html rendered}
			{:else}
				<p class="text-text-tertiary italic">Nothing to preview</p>
			{/if}
		</div>
	{/if}
</div>
