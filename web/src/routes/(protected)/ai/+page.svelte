<script lang="ts">
	import { createMutation } from '@tanstack/svelte-query';
	import { Send, Sparkles } from '@lucide/svelte';
	import { ai } from '$lib/api/endpoints/ai';
	import { showError, apiErrorMessage } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';

	type Message = {
		role: 'user' | 'assistant';
		text: string;
	};

	let prompt = $state('');
	let messages = $state<Message[]>([
		{
			role: 'assistant',
			text: 'Ask me about leases, rent collection, maintenance, reports, scan results, notices, or rental accounting.'
		}
	]);

	const chatMutation = createMutation(() => ({
		mutationFn: (message: string) => ai.chat(message),
		onSuccess: (response) => {
			messages = [...messages, { role: 'assistant', text: response.reply }];
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function sendMessage() {
		const message = prompt.trim();
		if (!message || chatMutation.isPending) return;

		messages = [...messages, { role: 'user', text: message }];
		prompt = '';
		chatMutation.mutate(message);
	}
</script>

<svelte:head>
	<title>AI Assistant - Rental Command</title>
</svelte:head>

<div class="flex h-full flex-col overflow-hidden p-4 sm:p-6" data-testid="ai-page">
	<div class="mb-4 flex items-start justify-between gap-3">
		<div class="min-w-0">
			<div class="mb-2 flex items-center gap-2 text-primary">
				<Sparkles class="h-5 w-5" />
				<span class="text-sm font-medium">AI Assistant</span>
			</div>
			<h1 class="text-2xl font-bold">Rental Command Assistant</h1>
			<p class="text-sm text-muted-foreground">Help for property operations, scans, notices, accounting, and tenant workflows.</p>
		</div>
	</div>

	<div class="flex min-h-0 flex-1 flex-col rounded-lg border border-border bg-card">
		<div class="min-h-0 flex-1 space-y-3 overflow-y-auto p-3 sm:p-4" data-testid="ai-messages">
			{#each messages as message, index (`${message.role}-${index}`)}
				<div class="flex" class:justify-end={message.role === 'user'}>
					<div
						class="max-w-[min(44rem,92%)] whitespace-pre-wrap rounded-lg border border-border px-3 py-2 text-sm leading-6"
						class:bg-background={message.role === 'assistant'}
						class:bg-primary={message.role === 'user'}
						class:text-white={message.role === 'user'}
						data-testid={`ai-message-${message.role}`}
					>
						{message.text}
					</div>
				</div>
			{/each}
			{#if chatMutation.isPending}
				<div class="max-w-[min(44rem,92%)] rounded-lg border border-border bg-background px-3 py-2 text-sm text-muted-foreground" data-testid="ai-loading">
					Thinking...
				</div>
			{/if}
		</div>

		<form class="border-t border-border p-3 sm:p-4" onsubmit={(event) => { event.preventDefault(); sendMessage(); }}>
			<label class="mb-2 block text-xs font-medium text-muted-foreground" for="ai-prompt-input">Message</label>
			<div class="grid gap-2 sm:grid-cols-[1fr_auto]">
				<textarea
					id="ai-prompt-input"
					data-testid="ai-prompt-input"
					bind:value={prompt}
					rows={3}
					class="min-h-24 w-full resize-y rounded border border-border bg-background px-3 py-2 text-sm"
					placeholder="Ask for a lease notice draft, explain a scan result, summarize overdue rent, or plan a maintenance follow-up."
				></textarea>
				<Button type="submit" disabled={!prompt.trim() || chatMutation.isPending} data-testid="ai-send-button" class="h-11 sm:self-end">
					<Send class="h-4 w-4" />
					Send
				</Button>
			</div>
		</form>
	</div>
</div>
