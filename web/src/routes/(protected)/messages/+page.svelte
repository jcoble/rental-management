<script lang="ts">
	import { page } from '$app/state';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { tick } from 'svelte';
	import { messages, type ConversationSummary, type ConversationMessage } from '$lib/api/endpoints/messages';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatRelative } from '$lib/utils/date';
	import { ApiError, type FairHousingConcern } from '$lib/api/client';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import FairHousingReviewDialog from '$lib/components/shared/FairHousingReviewDialog.svelte';
	import { Plus, MessageSquare, ArrowLeft, Send, MailWarning } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	// --- Selected thread --------------------------------------------------------
	let selectedId = $state<number | null>(null);
	$effect(() => {
		const queryId = Number(page.url.searchParams.get('conversation'));
		if (Number.isInteger(queryId) && queryId > 0 && selectedId !== queryId) {
			selectedId = queryId;
		}
	});

	// --- Conversation list (left pane) -----------------------------------------
	const conversationsQuery = createQuery(() => ({
		queryKey: ['conversations'],
		queryFn: () => messages.list(),
	}));

	const conversations = $derived(conversationsQuery.data ?? []);

	// --- Selected conversation (right pane) ------------------------------------
	const conversationQuery = createQuery(() => ({
		queryKey: ['conversation', selectedId],
		queryFn: () => messages.get(selectedId as number),
		enabled: selectedId !== null,
	}));

	const conversation = $derived(conversationQuery.data ?? null);

	// Opening a thread fetches it (server marks it read) → clear the unread badge
	// in the list by invalidating once the detail load resolves.
	let lastMarkedReadId = $state<number | null>(null);
	$effect(() => {
		const data = conversationQuery.data;
		if (data && data.id !== lastMarkedReadId) {
			lastMarkedReadId = data.id;
			queryClient.invalidateQueries({ queryKey: ['conversations'] });
		}
	});

	function openConversation(id: number) {
		selectedId = id;
	}

	function backToList() {
		selectedId = null;
	}

	// --- Portfolio + tenants (for compose) -------------------------------------
	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId, { take: 500 }),
	}));

	const portfolioQuery = createQuery(() => ({
		queryKey: ['portfolio', portfolioId],
		queryFn: () => portfolios.get(portfolioId),
	}));

	// Read Email/SMS defaults out of Portfolio.settings JSON ("messaging" key).
	// Portal is always the base channel and stays on regardless.
	const messagingDefaults = $derived.by(() => {
		try {
			const parsed = JSON.parse(portfolioQuery.data?.settings || '{}');
			const m = parsed?.messaging ?? {};
			return { email: m.email === true, sms: m.sms === true };
		} catch {
			return { email: false, sms: false };
		}
	});

	const tenantOptions = $derived(
		(tenantsQuery.data ?? []).map((t) => ({
			id: t.id,
			name: t.fullName ?? `${t.firstName} ${t.lastName}`,
		}))
	);

	function channelsToList(c: { portal: boolean; email: boolean; sms: boolean }): string[] {
		const selected: string[] = [];
		if (c.portal) selected.push('Portal');
		if (c.email) selected.push('Email');
		if (c.sms) selected.push('Sms');
		return selected;
	}

	// --- Reply compose (right pane, pinned bottom) -----------------------------
	let replyBody = $state('');
	let replyChannels = $state({ portal: true, email: false, sms: false });

	// L-15: the reply box must be cleared ONLY on an actual thread switch — never when the
	// portfolio query resolves/invalidates (e.g. a SignalR Portfolio event), which would
	// silently erase a half-typed reply. Split into two effects:
	//
	//   1. Thread switch (depends only on selectedId): clear the body + reset channels to base.
	//   2. Channel-defaults seed (once per thread): apply the saved email/sms defaults. This
	//      handles the case where defaults arrive AFTER the thread was opened, without touching
	//      replyBody — so a later portfolio refresh can't wipe the draft.
	let channelsSeededFor = $state<number | null>(null);
	$effect(() => {
		void selectedId; // thread switch is the only trigger
		replyBody = '';
		replyChannels = { portal: true, email: false, sms: false };
		channelsSeededFor = null;
	});
	$effect(() => {
		const defaults = messagingDefaults; // re-run when defaults become available/change
		if (selectedId !== null && channelsSeededFor !== selectedId) {
			channelsSeededFor = selectedId;
			replyChannels = { portal: true, email: defaults.email, sms: defaults.sms };
		}
	});

	const replyAnyChannel = $derived(replyChannels.portal || replyChannels.email || replyChannels.sms);
	const canSendReply = $derived(!!replyBody.trim() && replyAnyChannel && selectedId !== null);

	const replyMutation = createMutation(() => ({
		mutationFn: () =>
			messages.sendMessage(selectedId as number, {
				body: replyBody.trim(),
				channels: channelsToList(replyChannels),
			}),
		onSuccess: (updated) => {
			replyBody = '';
			// Seed the detail cache with the server's fresh thread, then refresh the list.
			queryClient.setQueryData(['conversation', updated.id], updated);
			queryClient.invalidateQueries({ queryKey: ['conversation', updated.id] });
			queryClient.invalidateQueries({ queryKey: ['conversations'] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
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
	const composeEmpty = { tenantId: '', subject: '', body: '' };
	let composeForm = $state({ ...composeEmpty });
	let composeChannels = $state({ portal: true, email: false, sms: false });

	const selectedTenantName = $derived(
		tenantOptions.find((t) => String(t.id) === composeForm.tenantId)?.name ?? null
	);

	const composeAnyChannel = $derived(
		composeChannels.portal || composeChannels.email || composeChannels.sms
	);
	const canStart = $derived(
		!!composeForm.tenantId &&
			!!composeForm.subject.trim() &&
			!!composeForm.body.trim() &&
			composeAnyChannel
	);

	function openCompose() {
		composeForm = { ...composeEmpty };
		composeChannels = { portal: true, email: messagingDefaults.email, sms: messagingDefaults.sms };
		composeOpen = true;
	}

	function closeCompose() {
		composeOpen = false;
		composeForm = { ...composeEmpty };
	}

	// Fair Housing review gate: a 422 from POST /conversations means the copy was flagged. We show the
	// concerns and let the landlord either Edit (revise) or send anyway (re-submit with the ack flag).
	let fhReviewOpen = $state(false);
	let fhDetail = $state('');
	let fhConcerns = $state<FairHousingConcern[]>([]);

	const startMutation = createMutation(() => ({
		mutationFn: (acknowledgedFairHousingReview: boolean = false) =>
			messages.start({
				tenantId: Number(composeForm.tenantId),
				subject: composeForm.subject.trim(),
				body: composeForm.body.trim(),
				channels: channelsToList(composeChannels),
				...(acknowledgedFairHousingReview ? { acknowledgedFairHousingReview: true } : {}),
			}),
		onSuccess: (created) => {
			fhReviewOpen = false;
			queryClient.setQueryData(['conversation', created.id], created);
			queryClient.invalidateQueries({ queryKey: ['conversations'] });
			closeCompose();
			openConversation(created.id);
		},
		onError: (err) => {
			if (err instanceof ApiError && err.fairHousingConcerns) {
				fhDetail = err.message;
				fhConcerns = err.fairHousingConcerns;
				fhReviewOpen = true;
				return;
			}
			showError(apiErrorMessage(err));
		},
	}));

	function startConversation() {
		if (!canStart || startMutation.isPending) return;
		startMutation.mutate(false);
	}

	function editFairHousing() {
		// Dismiss the review so the landlord can revise the copy in the still-open compose dialog.
		fhReviewOpen = false;
	}

	function sendAnyway() {
		if (startMutation.isPending) return;
		startMutation.mutate(true);
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

	function channelLabel(ch: string): string {
		if (ch === 'Sms') return 'Text';
		return ch;
	}
</script>

<svelte:head>
	<title>Messages - Rental Command</title>
</svelte:head>

<div class="flex h-full flex-col" data-testid="messages-page">
	<!-- Two-pane shell: list on the left, thread on the right. On narrow screens
	     only one pane shows at a time (list ↔ thread). -->
	<div class="flex min-h-0 flex-1 overflow-hidden">
		<!-- LEFT: thread list ------------------------------------------------ -->
		<aside
			class="flex w-full shrink-0 flex-col border-r border-border md:w-80 lg:w-96
				{selectedId !== null ? 'hidden md:flex' : 'flex'}"
			data-testid="conversation-list-pane"
		>
			<div class="flex items-center justify-between gap-2 border-b border-border p-4">
				<div class="min-w-0">
					<h1 class="text-lg font-bold leading-none">Messages</h1>
					<p class="mt-1 text-xs text-muted-foreground">Chat with your tenants.</p>
				</div>
				<Button size="sm" class="shrink-0 gap-1.5" data-testid="conversation-new-button" onclick={openCompose}>
					<Plus class="h-4 w-4" />
					New
				</Button>
			</div>

			<div class="min-h-0 flex-1 overflow-y-auto" data-testid="conversation-list">
				{#if conversationsQuery.isLoading}
					<div class="space-y-2 p-3">
						{#each [0, 1, 2, 3, 4] as _}
							<div class="h-16 animate-pulse rounded-lg bg-muted/60"></div>
						{/each}
					</div>
				{:else if conversationsQuery.isError}
					<div class="p-6 text-center text-sm text-destructive" data-testid="conversation-list-error">
						<MailWarning class="mx-auto mb-2 h-6 w-6" />
						Couldn't load your conversations.
						<button class="mt-2 block w-full text-xs underline" onclick={() => conversationsQuery.refetch()}>
							Try again
						</button>
					</div>
				{:else if conversations.length === 0}
					<div class="flex flex-col items-center justify-center px-6 py-16 text-center" data-testid="conversation-list-empty">
						<MessageSquare class="mb-3 h-8 w-8 text-muted-foreground" />
						<p class="text-sm font-medium">No conversations yet</p>
						<p class="mt-1 text-xs text-muted-foreground">Start one to message a tenant.</p>
						<Button size="sm" class="mt-4 gap-1.5" onclick={openCompose}>
							<Plus class="h-4 w-4" />
							New conversation
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
			data-testid="conversation-pane"
		>
			{#if selectedId === null}
				<!-- Empty / no-selection placeholder (desktop only). -->
				<div class="flex h-full flex-col items-center justify-center p-8 text-center text-muted-foreground">
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
				<div class="flex h-full flex-col items-center justify-center p-8 text-center text-destructive" data-testid="conversation-error">
					<MailWarning class="mb-2 h-8 w-8" />
					<p class="text-sm">Couldn't load this conversation.</p>
					<Button variant="outline" size="sm" class="mt-3" onclick={() => conversationQuery.refetch()}>Try again</Button>
				</div>
			{:else if conversation}
				<!-- Thread header -->
				<div class="flex items-center gap-2 border-b border-border p-4">
					<Button
						variant="ghost"
						size="icon"
						class="md:hidden"
						data-testid="conversation-back"
						onclick={backToList}
						aria-label="Back to conversations"
					>
						<ArrowLeft class="h-5 w-5" />
					</Button>
					<div class="min-w-0">
						<h2 class="truncate text-base font-semibold" data-testid="conversation-title">
							{conversation.subject}
						</h2>
						<p class="truncate text-xs text-muted-foreground">
							{conversation.tenantName}{conversation.propertyName ? ` · ${conversation.propertyName}` : ''}
						</p>
					</div>
				</div>

				<!-- Message history (chat bubbles) -->
				<div bind:this={scrollEl} class="min-h-0 flex-1 space-y-3 overflow-y-auto p-4" data-testid="conversation-messages">
					{#each conversation.messages as m (m.id)}
						{@render bubble(m)}
					{/each}
				</div>

				<!-- Compose box pinned at the bottom -->
				<div class="border-t border-border p-3">
					<div class="flex items-end gap-2">
						<textarea
							data-testid="reply-input"
							bind:value={replyBody}
							onkeydown={onReplyKeydown}
							rows={2}
							class="max-h-40 min-h-[2.5rem] flex-1 resize-y rounded-md border border-border bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
							placeholder="Type a message…  (Enter to send)"
						></textarea>
						<Button
							size="icon"
							class="h-10 w-10 shrink-0"
							data-testid="reply-send"
							onclick={sendReply}
							disabled={!canSendReply || replyMutation.isPending}
							aria-label="Send message"
						>
							<Send class="h-4 w-4" />
						</Button>
					</div>

					<!-- Channel picker for this reply (pre-filled from settings; per-send only) -->
					<div class="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs">
						<span class="text-muted-foreground">Send via:</span>
						<label class="flex items-center gap-1.5">
							<Checkbox bind:checked={replyChannels.portal} data-testid="reply-channel-portal" />
							<span>Portal</span>
						</label>
						<label class="flex items-center gap-1.5">
							<Checkbox bind:checked={replyChannels.email} data-testid="reply-channel-email" />
							<span>Email</span>
						</label>
						<label class="flex items-center gap-1.5">
							<Checkbox bind:checked={replyChannels.sms} data-testid="reply-channel-sms" />
							<span>Text</span>
						</label>
						{#if !replyAnyChannel}
							<span class="text-destructive" data-testid="reply-channel-error">Pick at least one.</span>
						{/if}
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
			data-testid="conversation-row"
			onclick={() => openConversation(c.id)}
		>
			<div class="min-w-0 flex-1">
				<div class="flex items-baseline justify-between gap-2">
					<span class="truncate text-sm font-semibold {c.unreadCount > 0 ? 'text-foreground' : ''}">
						{c.tenantName}
					</span>
					<span class="shrink-0 text-[11px] text-muted-foreground">{formatRelative(c.lastMessageAt)}</span>
				</div>
				<p class="truncate text-xs font-medium text-foreground/80">{c.subject}</p>
				{#if c.lastMessagePreview}
					<p class="truncate text-xs text-muted-foreground">{c.lastMessagePreview}</p>
				{/if}
			</div>
			{#if c.unreadCount > 0}
				<span
					class="mt-0.5 inline-flex h-5 min-w-5 shrink-0 items-center justify-center rounded-full bg-primary px-1.5 text-[11px] font-semibold text-primary-foreground"
					data-testid="conversation-unread-badge"
				>
					{c.unreadCount}
				</span>
			{/if}
		</button>
	</li>
{/snippet}

<!-- Chat bubble snippet -->
{#snippet bubble(m: ConversationMessage)}
	{@const mine = m.senderRole === 'Landlord'}
	<div class="flex {mine ? 'justify-end' : 'justify-start'}" data-testid="message-bubble">
		<div class="max-w-[78%] space-y-1">
			<div
				class="whitespace-pre-wrap break-words rounded-2xl px-3.5 py-2 text-sm leading-relaxed
					{mine
						? 'rounded-br-sm bg-primary text-primary-foreground'
						: 'rounded-bl-sm bg-muted text-foreground'}"
			>
				{m.body}
			</div>
			<div class="flex items-center gap-1.5 px-1 text-[11px] text-muted-foreground {mine ? 'justify-end' : 'justify-start'}">
				<span>{formatRelative(m.createdAt)}</span>
				{#if mine && m.channels}
					<span aria-hidden="true">·</span>
					<span>{m.channels.split(',').filter(Boolean).map(channelLabel).join(', ')}</span>
				{/if}
			</div>
		</div>
	</div>
{/snippet}

<!-- New conversation dialog -->
<Dialog.Root open={composeOpen} onOpenChange={(v) => { if (!v) closeCompose(); }}>
	<Dialog.Content class="max-w-xl">
		<Dialog.Header>
			<Dialog.Title>New conversation</Dialog.Title>
			<Dialog.Description>
				Start a thread with a tenant. Choose how the first message reaches them below.
			</Dialog.Description>
		</Dialog.Header>

		<div class="space-y-4" data-testid="conversation-compose-form">
			<!-- Recipient -->
			<div class="space-y-1">
				<span class="text-xs font-medium uppercase tracking-wide text-muted-foreground">To</span>
				<Select.Root type="single" bind:value={composeForm.tenantId}>
					<Select.Trigger class="w-full" data-testid="conversation-compose-tenant">
						{selectedTenantName ?? 'Choose a tenant…'}
					</Select.Trigger>
					<Select.Content>
						{#if tenantOptions.length === 0}
							<Select.Item value="" label="No tenants" disabled>No tenants found</Select.Item>
						{:else}
							{#each tenantOptions as t}
								<Select.Item value={String(t.id)} label={t.name}>{t.name}</Select.Item>
							{/each}
						{/if}
					</Select.Content>
				</Select.Root>
			</div>

			<!-- Subject (topic) -->
			<div class="space-y-1">
				<label for="conversation-compose-subject" class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Topic</label>
				<Input
					id="conversation-compose-subject"
					data-testid="conversation-compose-subject"
					bind:value={composeForm.subject}
					placeholder="What's this about?"
				/>
			</div>

			<!-- First message -->
			<div class="space-y-1">
				<label for="conversation-compose-body" class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Message</label>
				<textarea
					id="conversation-compose-body"
					data-testid="conversation-compose-body"
					bind:value={composeForm.body}
					rows={4}
					class="w-full rounded border border-border bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
					placeholder="Type your message…"
				></textarea>
			</div>

			<!-- Channels -->
			<div class="space-y-2 rounded-lg border border-border bg-muted/30 p-3">
				<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Send via</p>
				<label class="flex items-start gap-3">
					<Checkbox bind:checked={composeChannels.portal} data-testid="conversation-channel-portal" />
					<span class="text-sm leading-tight">
						<span class="font-medium">Portal inbox</span>
						<span class="block text-xs text-muted-foreground">Shows up in the tenant's account.</span>
					</span>
				</label>
				<label class="flex items-start gap-3">
					<Checkbox bind:checked={composeChannels.email} data-testid="conversation-channel-email" />
					<span class="text-sm leading-tight">
						<span class="font-medium">Email</span>
						<span class="block text-xs text-muted-foreground">Emails the tenant a copy.</span>
					</span>
				</label>
				<label class="flex items-start gap-3">
					<Checkbox bind:checked={composeChannels.sms} data-testid="conversation-channel-sms" />
					<span class="text-sm leading-tight">
						<span class="font-medium">Text (SMS)</span>
						<span class="block text-xs text-muted-foreground">Texts the tenant. Requires SMS setup.</span>
					</span>
				</label>
				{#if !composeAnyChannel}
					<p class="text-xs text-destructive" data-testid="conversation-channel-error">Pick at least one way to send.</p>
				{/if}
			</div>
		</div>

		<Dialog.Footer>
			<Button variant="outline" onclick={closeCompose}>Cancel</Button>
			<Button
				data-testid="conversation-compose-send"
				onclick={startConversation}
				disabled={!canStart || startMutation.isPending}
			>
				{startMutation.isPending ? 'Starting…' : 'Start conversation'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<!-- Fair Housing review gate (422 on send) -->
<FairHousingReviewDialog
	open={fhReviewOpen}
	detail={fhDetail}
	concerns={fhConcerns}
	busy={startMutation.isPending}
	testid="conversation-fair-housing"
	onedit={editFairHousing}
	onsendanyway={sendAnyway}
/>
