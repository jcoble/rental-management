<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { tick } from 'svelte';
	import {
		portal,
		type ConversationSummary,
		type ConversationMessage
	} from '$lib/api/endpoints/portal';
	import { conversationReadKey, markConversationRead } from '$lib/messages/conversation-read-state';
	import { conversationSelectionUrl, readConversationId } from '$lib/messages/conversation-url-state';
	import { showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatRelative } from '$lib/utils/date';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Plus, MessageSquare, ArrowLeft, Send, MailWarning, ChevronLeft } from '@lucide/svelte';

	const queryClient = useQueryClient();

	// --- Selected thread --------------------------------------------------------
	let selectedId = $state<number | null>(null);

	$effect(() => {
		const fromUrl = readConversationId(page.url.searchParams);
		if (selectedId !== fromUrl) selectedId = fromUrl;
	});

	// --- Conversation list (left pane) -----------------------------------------
	const conversationsQuery = createQuery(() => ({
		queryKey: ['portal-conversations'],
		queryFn: () => portal.conversations.list()
	}));

	const conversations = $derived(conversationsQuery.data ?? []);

	// --- Selected conversation (right pane) ------------------------------------
	const conversationQuery = createQuery(() => ({
		queryKey: ['portal-conversation', selectedId],
		queryFn: () => portal.conversations.get(selectedId as number),
		enabled: selectedId !== null
	}));

	const conversation = $derived(conversationQuery.data ?? null);

	function markPortalConversationRead(id: number) {
		queryClient.setQueryData(['portal-conversations'], (items: ConversationSummary[] | undefined) =>
			markConversationRead(items ?? [], id)
		);
		queryClient.invalidateQueries({ queryKey: ['portal-conversations'] });
	}

	// Opening a thread fetches it (server marks it read for the tenant). Track the
	// current message version, not just the id, so a new live reply in the already-open
	// thread clears the list/dashboard unread state after the detail refetch marks it read.
	let lastMarkedReadKey = $state<string | null>(null);
	$effect(() => {
		const data = conversationQuery.data;
		const readKey = conversationReadKey(data);
		if (data && readKey !== lastMarkedReadKey) {
			lastMarkedReadKey = readKey;
			markPortalConversationRead(data.id);
		}
	});

	function openConversation(id: number) {
		selectedId = id;
		void goto(conversationSelectionUrl(page.url, id), {
			replaceState: true,
			noScroll: true,
			keepFocus: true
		});
	}

	function backToList() {
		selectedId = null;
		void goto(conversationSelectionUrl(page.url, null), {
			replaceState: true,
			noScroll: true,
			keepFocus: true
		});
	}

	// --- Reply compose (right pane, pinned bottom) -----------------------------
	let replyBody = $state('');
	let replyOperationKey = $state<string | null>(null);

	// Reset the reply box whenever the active thread changes.
	$effect(() => {
		void selectedId;
		replyBody = '';
		replyOperationKey = null;
	});

	const canSendReply = $derived(!!replyBody.trim() && selectedId !== null);

	const replyMutation = createMutation(() => ({
		mutationFn: () =>
			portal.conversations.sendMessage(selectedId as number, {
				operationKey: (replyOperationKey ??= crypto.randomUUID()),
				body: replyBody.trim()
			}),
		onSuccess: (updated) => {
			replyBody = '';
			replyOperationKey = null;
			// Seed the detail cache with the server's fresh thread so the tenant's own
			// message shows instantly, then refresh both keys (don't depend on the socket).
			queryClient.setQueryData(['portal-conversation', updated.id], updated);
			queryClient.invalidateQueries({ queryKey: ['portal-conversation', updated.id] });
			queryClient.invalidateQueries({ queryKey: ['portal-conversations'] });
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function sendReply() {
		if (!canSendReply || replyMutation.isPending) return;
		replyMutation.mutate();
	}

	// Enter sends, Shift+Enter makes a newline.
	function onReplyKeydown(e: KeyboardEvent) {
		if (e.key === 'Enter' && !e.shiftKey) {
			e.preventDefault();
			sendReply();
		}
	}

	// --- New conversation dialog -----------------------------------------------
	let composeOpen = $state(false);
	let composeOperationKey = $state<string | null>(null);
	const composeEmpty = { subject: '', body: '' };
	let composeForm = $state({ ...composeEmpty });

	const canStart = $derived(!!composeForm.subject.trim() && !!composeForm.body.trim());

	function openCompose() {
		composeForm = { ...composeEmpty };
		composeOperationKey = null;
		composeOpen = true;
	}

	function closeCompose() {
		composeOpen = false;
		composeForm = { ...composeEmpty };
		composeOperationKey = null;
	}

	const startMutation = createMutation(() => ({
		mutationFn: () =>
			portal.conversations.start({
				operationKey: (composeOperationKey ??= crypto.randomUUID()),
				subject: composeForm.subject.trim(),
				body: composeForm.body.trim()
			}),
		onSuccess: (created) => {
			queryClient.setQueryData(['portal-conversation', created.id], created);
			queryClient.invalidateQueries({ queryKey: ['portal-conversations'] });
			closeCompose();
			openConversation(created.id);
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function startConversation() {
		if (!canStart || startMutation.isPending) return;
		startMutation.mutate();
	}

	// --- Auto-scroll the thread to the newest message --------------------------
	let scrollEl = $state<HTMLElement | null>(null);
	$effect(() => {
		// Rerun whenever the message list grows or the thread changes.
		const count = conversation?.messages.length ?? 0;
		void count;
		void selectedId;
		if (!scrollEl) return;
		tick().then(() => {
			if (scrollEl) scrollEl.scrollTop = scrollEl.scrollHeight;
		});
	});
</script>

<svelte:head>
	<title>Messages - Rental Command</title>
</svelte:head>

<div class="flex h-full min-h-[720px] flex-col" data-testid="portal-messages-page">
	<!-- Two-pane shell: thread list on the left, conversation on the right. On
	     narrow screens only one pane shows at a time (list ↔ thread). -->
	<div class="flex min-h-0 flex-1 overflow-hidden">
		<!-- LEFT: thread list ------------------------------------------------ -->
		<aside
			class="flex w-full shrink-0 flex-col border-r border-border md:w-80 lg:w-96
				{selectedId !== null ? 'hidden md:flex' : 'flex'}"
			data-testid="portal-conversation-list-pane"
		>
			<div class="flex items-center justify-between gap-2 border-b border-border p-4">
				<div class="flex min-w-0 items-center gap-2">
					<a
						href="/portal"
						class="-ml-1 inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-muted"
						aria-label="Back to portal"
						data-testid="portal-messages-home"
					>
						<ChevronLeft class="h-4 w-4" />
					</a>
					<div class="min-w-0">
						<h1 class="text-lg font-bold leading-none">Messages</h1>
						<p class="mt-1 text-xs text-muted-foreground">Chat with your management team.</p>
					</div>
				</div>
				<Button
					size="sm"
					class="shrink-0 gap-1.5"
					data-testid="portal-conversation-new-button"
					onclick={openCompose}
				>
					<Plus class="h-4 w-4" />
					New
				</Button>
			</div>

			<div class="min-h-0 flex-1 overflow-y-auto" data-testid="portal-conversation-list">
				{#if conversationsQuery.isLoading}
					<div class="space-y-2 p-3">
						{#each [0, 1, 2, 3, 4] as _}
							<div class="h-16 animate-pulse rounded-lg bg-muted/60"></div>
						{/each}
					</div>
				{:else if conversationsQuery.isError}
					<div
						class="p-6 text-center text-sm text-destructive"
						data-testid="portal-conversation-list-error"
					>
						<MailWarning class="mx-auto mb-2 h-6 w-6" />
						Couldn't load your messages.
						<button
							class="mt-2 block w-full text-xs underline"
							onclick={() => conversationsQuery.refetch()}
						>
							Try again
						</button>
					</div>
				{:else if conversations.length === 0}
					<div
						class="flex flex-col items-center justify-center px-6 py-16 text-center"
						data-testid="portal-conversation-list-empty"
					>
						<MessageSquare class="mb-3 h-8 w-8 text-muted-foreground" />
						<p class="text-sm font-medium">No messages yet</p>
						<p class="mt-1 text-xs text-muted-foreground">
							Start a conversation with your management team.
						</p>
						<Button size="sm" class="mt-4 gap-1.5" onclick={openCompose}>
							<Plus class="h-4 w-4" />
							New message
						</Button>
					</div>
				{:else}
					<ul>
						{#each conversations as c (c.id)}
							{@render conversationRow(c)}
						{/each}
					</ul>
				{/if}
			</div>
		</aside>

		<!-- RIGHT: conversation view ----------------------------------------- -->
		<section
			class="min-w-0 flex-1 flex-col {selectedId !== null ? 'flex' : 'hidden md:flex'}"
			data-testid="portal-conversation-pane"
		>
			{#if selectedId === null}
				<!-- Empty / no-selection placeholder (desktop only). -->
				<div
					class="flex h-full flex-col items-center justify-center p-8 text-center text-muted-foreground"
				>
					<MessageSquare class="mb-3 h-10 w-10" />
					<p class="text-sm font-medium">Select a conversation</p>
					<p class="mt-1 text-xs">Pick a thread on the left, or start a new one.</p>
				</div>
			{:else if conversationQuery.isLoading}
				<div class="flex-1 space-y-3 p-4">
					{#each [0, 1, 2, 3] as _}
						<div class="h-14 w-2/3 animate-pulse rounded-2xl bg-muted/60"></div>
					{/each}
				</div>
			{:else if conversationQuery.isError}
				<div
					class="flex h-full flex-col items-center justify-center p-8 text-center text-destructive"
					data-testid="portal-conversation-error"
				>
					<MailWarning class="mb-2 h-8 w-8" />
					<p class="text-sm">Couldn't load this conversation.</p>
					<Button variant="outline" size="sm" class="mt-3" onclick={() => conversationQuery.refetch()}>
						Try again
					</Button>
				</div>
			{:else if conversation}
				<!-- Thread header -->
				<div class="flex items-center gap-2 border-b border-border p-4">
					<Button
						variant="ghost"
						size="icon"
						class="md:hidden"
						data-testid="portal-conversation-back"
						onclick={backToList}
						aria-label="Back to messages"
					>
						<ArrowLeft class="h-5 w-5" />
					</Button>
					<div class="min-w-0">
						<h2 class="truncate text-base font-semibold" data-testid="portal-conversation-title">
							{conversation.subject}
						</h2>
						<p class="truncate text-xs text-muted-foreground">
							Management{conversation.propertyName ? ` · ${conversation.propertyName}` : ''}
						</p>
					</div>
				</div>

				<!-- Message history (chat bubbles) -->
				<div
					bind:this={scrollEl}
					class="min-h-0 flex-1 space-y-3 overflow-y-auto p-4"
					data-testid="portal-conversation-messages"
				>
					{#each conversation.messages as m (m.id)}
						{@render bubble(m)}
					{/each}
				</div>

				<!-- Compose box pinned at the bottom (no channel picker on the tenant side) -->
				<div class="border-t border-border p-3">
					<div class="flex items-end gap-2">
						<textarea
							data-testid="portal-reply-input"
							bind:value={replyBody}
							onkeydown={onReplyKeydown}
							rows={2}
							class="max-h-40 min-h-[2.5rem] flex-1 resize-y rounded-md border border-border bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
							placeholder="Type a message…  (Enter to send)"
						></textarea>
						<Button
							size="icon"
							class="h-10 w-10 shrink-0"
							data-testid="portal-reply-send"
							onclick={sendReply}
							disabled={!canSendReply || replyMutation.isPending}
							aria-label="Send message"
						>
							<Send class="h-4 w-4" />
						</Button>
					</div>
				</div>
			{/if}
		</section>
	</div>
</div>

<!-- Thread-list row snippet -->
{#snippet conversationRow(c: ConversationSummary)}
	<li>
		<button
			type="button"
			class="flex w-full items-start gap-3 border-b border-border/60 px-4 py-3 text-left transition-colors hover:bg-muted/50
				{selectedId === c.id ? 'bg-muted' : ''}"
			data-testid="portal-conversation-row"
			onclick={() => openConversation(c.id)}
		>
			<div class="min-w-0 flex-1">
				<div class="flex items-baseline justify-between gap-2">
					<span class="truncate text-sm font-semibold {c.unreadCount > 0 ? 'text-foreground' : ''}">
						{c.subject}
					</span>
					<span class="shrink-0 text-[11px] text-muted-foreground">{formatRelative(c.lastMessageAt)}</span>
				</div>
				{#if c.lastMessagePreview}
					<p class="truncate text-xs text-muted-foreground">{c.lastMessagePreview}</p>
				{/if}
			</div>
			{#if c.unreadCount > 0}
				<span
					class="mt-0.5 inline-flex h-5 min-w-5 shrink-0 items-center justify-center rounded-full bg-primary px-1.5 text-[11px] font-semibold text-primary-foreground"
					data-testid="portal-conversation-unread-badge"
				>
					{c.unreadCount}
				</span>
			{/if}
		</button>
	</li>
{/snippet}

<!-- Chat bubble snippet (inverted from the landlord side: the tenant's own
     messages are right-aligned/primary, the landlord's are left-aligned/muted). -->
{#snippet bubble(m: ConversationMessage)}
	{@const mine = m.senderRole === 'Tenant'}
	<div class="flex {mine ? 'justify-end' : 'justify-start'}" data-testid="portal-message-bubble">
		<div class="max-w-[78%] space-y-1">
			{#if !mine}
				<div class="px-1 text-[11px] font-medium text-muted-foreground">Management</div>
			{/if}
			<div
				class="whitespace-pre-wrap break-words rounded-2xl px-3.5 py-2 text-sm leading-relaxed
					{mine
					? 'rounded-br-sm bg-primary text-primary-foreground'
					: 'rounded-bl-sm bg-muted text-foreground'}"
			>
				{m.body}
			</div>
			<div
				class="flex items-center gap-1.5 px-1 text-[11px] text-muted-foreground {mine
					? 'justify-end'
					: 'justify-start'}"
			>
				<span>{formatRelative(m.createdAt)}</span>
			</div>
		</div>
	</div>
{/snippet}

<!-- New conversation dialog (no recipient, no channel picker) -->
<Dialog.Root open={composeOpen} onOpenChange={(v) => { if (!v) closeCompose(); }}>
	<Dialog.Content class="max-w-xl">
		<Dialog.Header>
			<Dialog.Title>New message</Dialog.Title>
			<Dialog.Description>
				Send a message to your management team. They'll reply right here in the portal.
			</Dialog.Description>
		</Dialog.Header>

		<div class="space-y-4" data-testid="portal-conversation-compose-form">
			<!-- Subject (topic) -->
			<div class="space-y-1">
				<label
					for="portal-conversation-compose-subject"
					class="text-xs font-medium uppercase tracking-wide text-muted-foreground"
				>
					Topic
				</label>
				<Input
					id="portal-conversation-compose-subject"
					data-testid="portal-conversation-compose-subject"
					bind:value={composeForm.subject}
					placeholder="What's this about?"
				/>
			</div>

			<!-- First message -->
			<div class="space-y-1">
				<label
					for="portal-conversation-compose-body"
					class="text-xs font-medium uppercase tracking-wide text-muted-foreground"
				>
					Message
				</label>
				<textarea
					id="portal-conversation-compose-body"
					data-testid="portal-conversation-compose-body"
					bind:value={composeForm.body}
					rows={4}
					class="w-full rounded border border-border bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
					placeholder="Type your message…"
				></textarea>
			</div>
		</div>

		<Dialog.Footer>
			<Button variant="outline" onclick={closeCompose}>Cancel</Button>
			<Button
				data-testid="portal-conversation-compose-send"
				onclick={startConversation}
				disabled={!canStart || startMutation.isPending}
			>
				{startMutation.isPending ? 'Sending…' : 'Send message'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
