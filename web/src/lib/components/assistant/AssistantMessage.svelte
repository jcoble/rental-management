<script lang="ts">
	import type { AssistantChatMessage } from '$lib/stores/assistantChat.svelte';

	let { message }: { message: AssistantChatMessage } = $props();
</script>

<div class="flex {message.role === 'user' ? 'justify-end' : 'justify-start'}" data-testid="assistant-message-{message.role}">
	<div
		class="max-w-[85%] rounded-lg px-3 py-2 text-sm leading-relaxed {message.role === 'user'
			? 'bg-primary text-primary-foreground'
			: message.isError
				? 'border border-destructive/30 bg-destructive/10 text-destructive'
				: 'border border-border bg-background text-foreground'}"
	>
		{#if message.isLoading}
			<span class="text-muted-foreground">Thinking...</span>
		{:else}
			<p class="whitespace-pre-wrap">{message.content}</p>
		{/if}
	</div>
</div>
