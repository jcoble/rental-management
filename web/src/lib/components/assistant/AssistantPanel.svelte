<script lang="ts">
	import { Send, Trash2, X } from '@lucide/svelte';
	import { assistantChat } from '$lib/stores/assistantChat.svelte';
	import AssistantMessage from './AssistantMessage.svelte';

	let input = $state('');
	let messagesEl: HTMLDivElement | undefined = $state();

	$effect(() => {
		assistantChat.messages.length;
		assistantChat.messages.at(-1)?.content;
		if (messagesEl) {
			requestAnimationFrame(() => {
				if (messagesEl) messagesEl.scrollTop = messagesEl.scrollHeight;
			});
		}
	});

	function send() {
		if (!input.trim() || assistantChat.isLoading) return;
		const message = input;
		input = '';
		assistantChat.send(message);
	}

	function onKeydown(event: KeyboardEvent) {
		if (event.key === 'Enter' && !event.shiftKey) {
			event.preventDefault();
			send();
		}
	}
</script>

<div
	class="fixed bottom-20 right-5 z-50 flex max-h-[min(620px,calc(100dvh-6rem))] w-[min(420px,calc(100vw-2rem))] flex-col rounded-lg border border-border bg-card shadow-2xl"
	data-testid="assistant-panel"
>
	<div class="flex items-center justify-between border-b border-border px-4 py-3">
		<div>
			<h2 class="text-sm font-semibold">AI Assistant</h2>
			<p class="text-xs text-muted-foreground">Ask about properties, tenants, work orders, and accounting.</p>
		</div>
		<div class="flex items-center gap-1">
			<button
				type="button"
				class="rounded-md p-1.5 text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
				title="Clear conversation"
				aria-label="Clear conversation"
				data-testid="assistant-clear"
				onclick={() => assistantChat.clear()}
			>
				<Trash2 class="h-4 w-4" />
			</button>
			<button
				type="button"
				class="rounded-md p-1.5 text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
				title="Close assistant"
				aria-label="Close assistant"
				data-testid="assistant-close"
				onclick={() => assistantChat.close()}
			>
				<X class="h-4 w-4" />
			</button>
		</div>
	</div>

	<div bind:this={messagesEl} class="min-h-60 flex-1 space-y-3 overflow-y-auto px-4 py-3" data-testid="assistant-messages">
		{#if assistantChat.messages.length === 0}
			<div class="flex h-full min-h-48 items-center justify-center text-center text-sm text-muted-foreground">
				Ask for today's priorities, tenant history, open repairs, lease risk, or accounting context.
			</div>
		{:else}
			{#each assistantChat.messages as message (message.id)}
				<AssistantMessage {message} />
			{/each}
		{/if}
	</div>

	{#if assistantChat.error}
		<div class="border-t border-border px-4 py-2 text-xs text-destructive" data-testid="assistant-error">
			{assistantChat.error}
		</div>
	{/if}

	<div class="border-t border-border px-3 py-3">
		<div class="flex items-end gap-2">
			<textarea
				bind:value={input}
				onkeydown={onKeydown}
				rows="2"
				maxlength="2000"
				placeholder="Ask a question..."
				class="min-h-10 flex-1 resize-none rounded-md border border-input bg-background px-3 py-2 text-sm outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
				data-testid="assistant-input"
			></textarea>
			<button
				type="button"
				class="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-md bg-primary text-primary-foreground transition-colors hover:bg-primary/90 disabled:pointer-events-none disabled:opacity-50"
				disabled={!input.trim() || assistantChat.isLoading}
				aria-label="Send"
				data-testid="assistant-send"
				onclick={send}
			>
				<Send class="h-4 w-4" />
			</button>
		</div>
	</div>
</div>
