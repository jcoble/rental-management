<script lang="ts">
	import { Moon, Sun } from '@lucide/svelte';
	import { mode, setMode } from 'mode-watcher';
	import { Button } from '$lib/components/ui/button';

	let {
		class: className = '',
		'data-testid': dataTestId = 'theme-mode-toggle'
	}: {
		class?: string;
		'data-testid'?: string;
	} = $props();

	const isLight = $derived(mode.current === 'light');
	const nextMode = $derived(isLight ? 'dark' : 'light');
	const label = $derived(`Switch to ${nextMode} theme`);

	function toggleMode() {
		setMode(nextMode);
	}
</script>

<Button
	variant="ghost"
	size="icon"
	class={className}
	onclick={toggleMode}
	aria-label={label}
	data-m3-tooltip={label}
	data-testid={dataTestId}
>
	{#if isLight}
		<Moon class="h-5 w-5" aria-hidden="true" />
	{:else}
		<Sun class="h-5 w-5" aria-hidden="true" />
	{/if}
</Button>
