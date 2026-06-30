<script lang="ts">
	import { Mail, MessageSquare } from '@lucide/svelte';
	import { assistantChat, type AssistantChatMessage } from '$lib/stores/assistantChat.svelte';
	import AssistantActionDraftCard from './AssistantActionDraftCard.svelte';

	let { message }: { message: AssistantChatMessage } = $props();

	const canDeliver = $derived(
		message.role === 'assistant' && !message.isLoading && !message.isError && !!message.question
	);
</script>

<div class="flex {message.role === 'user' ? 'justify-end' : 'justify-start'}" data-testid="assistant-message-{message.role}">
	<div class="flex max-w-[85%] flex-col gap-1">
		<div
			class="rounded-lg px-3 py-2 text-sm leading-relaxed {message.role === 'user'
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

		{#if message.role === 'assistant' && (message.actionDraft || message.actionNote)}
			<AssistantActionDraftCard
				draft={message.actionDraft}
				status={message.actionStatus}
				missingFields={message.actionMissingFields ?? []}
				writeModeEnabled={assistantChat.writeModeEnabled}
				isExecuting={message.isExecutingAction}
				note={message.actionNote}
				onConfirm={() => assistantChat.executeAction(message.id)}
			/>
		{/if}

		{#if canDeliver}
			<div class="flex items-center gap-2 px-1" data-testid="assistant-deliver">
				<button
					type="button"
					class="inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-xs text-muted-foreground transition-colors hover:bg-muted hover:text-foreground disabled:pointer-events-none disabled:opacity-50"
					disabled={!!message.deliveringChannel}
					onclick={() => assistantChat.deliver(message.id, 'sms')}
					data-testid="assistant-deliver-sms"
				>
					<MessageSquare class="h-3.5 w-3.5" />
					{message.deliveringChannel === 'sms' ? 'Texting...' : 'Text me this'}
				</button>
				<button
					type="button"
					class="inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-xs text-muted-foreground transition-colors hover:bg-muted hover:text-foreground disabled:pointer-events-none disabled:opacity-50"
					disabled={!!message.deliveringChannel}
					onclick={() => assistantChat.deliver(message.id, 'email')}
					data-testid="assistant-deliver-email"
				>
					<Mail class="h-3.5 w-3.5" />
					{message.deliveringChannel === 'email' ? 'Emailing...' : 'Email me this'}
				</button>
			</div>
			{#if message.deliveryNote}
				<p class="px-1 text-xs text-muted-foreground" data-testid="assistant-deliver-note">
					{message.deliveryNote}
				</p>
			{/if}
		{/if}
	</div>
</div>
