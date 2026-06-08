<script lang="ts">
	import { MessageCircle, X } from '@lucide/svelte';
	import { assistantChat } from '$lib/stores/assistantChat.svelte';
	import AssistantPanel from './AssistantPanel.svelte';
</script>

{#if assistantChat.isOpen}
	<AssistantPanel />
{/if}

<button
	type="button"
	class="m3-state-layer fixed bottom-5 right-5 z-50 inline-flex h-12 w-12 items-center justify-center rounded-full bg-primary text-primary-foreground shadow-lg transition-transform hover:scale-105 active:scale-95"
	data-m3-tooltip={assistantChat.isOpen ? 'Close AI assistant' : 'Open AI assistant'}
	aria-label={assistantChat.isOpen ? 'Close AI assistant' : 'Open AI assistant'}
	data-testid="assistant-bubble"
	onclick={() => assistantChat.toggle()}
>
	{#if assistantChat.isOpen}
		<X class="h-5 w-5" />
	{:else}
		<MessageCircle class="h-5 w-5" />
	{/if}
</button>
