<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { messages } from '$lib/api/endpoints/messages';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import type { Message } from '$lib/types';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Dialog from '$lib/components/ui/dialog';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import { Plus } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const MSG_STATUSES = ['Open', 'InProgress', 'Resolved', 'Closed'];

	let statusFilter = $state('');
	let selectedMessage = $state<Message | null>(null);
	let replyText = $state('');

	const messagesQuery = createQuery(() => ({
		queryKey: ['messages', statusFilter],
		queryFn: () => messages.list(statusFilter || undefined),
	}));

	// --- Compose new message ---------------------------------------------------
	let composeOpen = $state(false);
	const composeEmpty = { tenantId: '', subject: '', body: '' };
	let composeForm = $state({ ...composeEmpty });
	// Portal is always the base channel; Email/SMS pre-fill from portfolio settings.
	let channels = $state({ portal: true, email: false, sms: false });

	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId, { take: 500 }),
	}));

	const portfolioQuery = createQuery(() => ({
		queryKey: ['portfolio', portfolioId],
		queryFn: () => portfolios.get(portfolioId),
	}));

	// Read Email/SMS defaults out of Portfolio.settings JSON ("messaging" key).
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
	const selectedTenantName = $derived(
		tenantOptions.find((t) => String(t.id) === composeForm.tenantId)?.name ?? null
	);

	const anyChannel = $derived(channels.portal || channels.email || channels.sms);
	const canSend = $derived(
		!!composeForm.tenantId && !!composeForm.subject.trim() && !!composeForm.body.trim() && anyChannel
	);

	function openCompose() {
		composeForm = { ...composeEmpty };
		// Portal always on; Email/SMS pre-filled from settings defaults (this message only).
		channels = { portal: true, email: messagingDefaults.email, sms: messagingDefaults.sms };
		composeOpen = true;
	}

	function closeCompose() {
		composeOpen = false;
		composeForm = { ...composeEmpty };
	}

	const composeMutation = createMutation(() => ({
		mutationFn: () => {
			const selected: string[] = [];
			if (channels.portal) selected.push('Portal');
			if (channels.email) selected.push('Email');
			if (channels.sms) selected.push('Sms');
			return messages.create({
				tenantId: Number(composeForm.tenantId),
				subject: composeForm.subject.trim(),
				body: composeForm.body.trim(),
				channels: selected,
			});
		},
		onSuccess: () => {
			showSuccess('Message sent.');
			closeCompose();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function sendCompose() {
		if (!canSend) return;
		composeMutation.mutate();
	}

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['messages'] });
	}

	const replyMutation = createMutation(() => ({
		mutationFn: ({ id, reply }: { id: number; reply: string }) =>
			messages.reply(id, { reply, status: 'InProgress' }),
		onSuccess: (updated) => {
			showSuccess('Reply sent.');
			selectedMessage = updated;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const statusMutation = createMutation(() => ({
		mutationFn: ({ id, status }: { id: number; status: string }) =>
			messages.setStatus(id, status),
		onSuccess: (updated) => {
			showSuccess('Status updated.');
			selectedMessage = updated;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openMessage(msg: Message) {
		selectedMessage = msg;
		replyText = msg.reply ?? '';
	}

	function closeDialog() {
		selectedMessage = null;
		replyText = '';
	}

	function sendReply() {
		if (!selectedMessage) return;
		replyMutation.mutate({ id: selectedMessage.id, reply: replyText });
	}

	function markResolved() {
		if (!selectedMessage) return;
		statusMutation.mutate({ id: selectedMessage.id, status: 'Resolved' });
	}

	function reopen() {
		if (!selectedMessage) return;
		statusMutation.mutate({ id: selectedMessage.id, status: 'Open' });
	}

	// Status badge map extended for message statuses
	const statusBadgeMap: Record<string, { label?: string; class: string }> = {
		Open:       { class: 'bg-blue-100 text-blue-800 border-blue-200 dark:bg-blue-900/30 dark:text-blue-400 dark:border-blue-800' },
		InProgress: { label: 'In Progress', class: 'bg-yellow-100 text-yellow-800 border-yellow-200 dark:bg-yellow-900/30 dark:text-yellow-500 dark:border-yellow-800' },
		Resolved:   { class: 'bg-green-100 text-green-800 border-green-200 dark:bg-green-900/30 dark:text-green-400 dark:border-green-800' },
		Closed:     { class: 'bg-muted text-muted-foreground border-border' },
	};

	// Column definitions
	const columns: ColumnDef<Message>[] = [
		{
			key: 'senderName',
			title: 'From',
			sortable: true,
			mobileRole: 'title',
			accessor: (m) => m.senderName ?? m.propertyName ?? '—',
			cell: fromCellSnippet,
		},
		{
			key: 'subject',
			title: 'Subject',
			sortable: true,
			mobileRole: 'subtitle',
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: statusCellSnippet,
		},
		{
			key: 'createdAt',
			title: 'Received',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
	];
</script>

{#snippet fromCellSnippet(msg: Message)}
	<span data-testid="message-from">{msg.senderName ?? msg.propertyName ?? '—'}</span>
{/snippet}

{#snippet statusCellSnippet(msg: Message)}
	<StatusBadge status={msg.status} map={statusBadgeMap} />
{/snippet}

<svelte:head>
	<title>Messages - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="messages-page">
	<div class="mb-6 flex items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Message Center</h1>
			<p class="text-sm text-muted-foreground">Tenant and owner messages — reply and track status.</p>
		</div>
	</div>

	<DataGrid
		data={messagesQuery.data ?? []}
		{columns}
		loading={messagesQuery.isLoading}
		emptyMessage="No messages found."
		onRowClick={(msg) => openMessage(msg)}
		getRowKey={(msg) => msg.id}
		getRowTestId={() => 'message-row'}
		data-testid="messages-list"
	>
		{#snippet toolbar()}
			<div class="flex flex-1 items-center gap-2 min-w-0">
				<Select.Root
					type="single"
					bind:value={statusFilter}
				>
					<Select.Trigger class="w-44 shrink-0" data-testid="message-status-filter">
						{statusFilter ? statusFilter : 'All statuses'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="All statuses">All statuses</Select.Item>
						{#each MSG_STATUSES as s}
							<Select.Item value={s} label={s}>{s}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			<Button data-testid="message-new-button" class="gap-2 shrink-0" onclick={openCompose}>
				<Plus class="h-4 w-4" />
				New message
			</Button>
		{/snippet}
	</DataGrid>
</div>

<!-- Message detail dialog -->
<Dialog.Root
	open={selectedMessage !== null}
	onOpenChange={(v) => { if (!v) closeDialog(); }}
>
	<Dialog.Content class="max-w-xl">
		{#if selectedMessage}
			<Dialog.Header>
				<Dialog.Title class="pr-4">{selectedMessage.subject}</Dialog.Title>
				<Dialog.Description class="flex items-center gap-2 flex-wrap">
					<span class="text-xs text-muted-foreground">
						From: <span class="font-medium text-foreground">{selectedMessage.senderName ?? '—'}</span>
					</span>
					{#if selectedMessage.propertyName}
						<span class="text-xs text-muted-foreground">· {selectedMessage.propertyName}{selectedMessage.unitLabel ? ` · ${selectedMessage.unitLabel}` : ''}</span>
					{/if}
					<StatusBadge status={selectedMessage.status} map={statusBadgeMap} />
				</Dialog.Description>
			</Dialog.Header>

			<!-- Message body -->
			<div class="rounded border border-border bg-muted/40 p-3 text-sm leading-relaxed" data-testid="message-body">
				{selectedMessage.body}
			</div>

			<!-- Existing reply -->
			{#if selectedMessage.reply}
				<div class="space-y-1">
					<p class="text-xs font-medium text-muted-foreground uppercase tracking-wide">Previous Reply</p>
					<div class="rounded border border-border bg-background p-3 text-sm leading-relaxed" data-testid="message-existing-reply">
						{selectedMessage.reply}
					</div>
				</div>
			{/if}

			<!-- Reply textarea -->
			<div class="space-y-1">
				<label for="message-reply-input" class="text-xs font-medium text-muted-foreground uppercase tracking-wide">
					{selectedMessage.reply ? 'Update Reply' : 'Write Reply'}
				</label>
				<textarea
					id="message-reply-input"
					data-testid="message-reply-input"
					bind:value={replyText}
					rows={4}
					class="w-full rounded border border-border bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
					placeholder="Type your reply…"
				></textarea>
			</div>

			<Dialog.Footer class="flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
				<!-- Status actions (left side) -->
				<div class="flex gap-2">
					{#if selectedMessage.status !== 'Resolved' && selectedMessage.status !== 'Closed'}
						<Button
							variant="outline"
							size="sm"
							data-testid="message-mark-resolved"
							onclick={markResolved}
							disabled={statusMutation.isPending}
						>
							Mark Resolved
						</Button>
					{/if}
					{#if selectedMessage.status === 'Resolved' || selectedMessage.status === 'Closed'}
						<Button
							variant="outline"
							size="sm"
							data-testid="message-reopen"
							onclick={reopen}
							disabled={statusMutation.isPending}
						>
							Reopen
						</Button>
					{/if}
				</div>

				<!-- Send reply (right side) -->
				<div class="flex gap-2">
					<Button variant="outline" onclick={closeDialog}>Cancel</Button>
					<Button
						data-testid="message-reply-send"
						onclick={sendReply}
						disabled={replyMutation.isPending || !replyText.trim()}
					>
						{replyMutation.isPending ? 'Sending…' : 'Send Reply'}
					</Button>
				</div>
			</Dialog.Footer>
		{/if}
	</Dialog.Content>
</Dialog.Root>

<!-- Compose new message dialog -->
<Dialog.Root open={composeOpen} onOpenChange={(v) => { if (!v) closeCompose(); }}>
	<Dialog.Content class="max-w-xl">
		<Dialog.Header>
			<Dialog.Title>New message</Dialog.Title>
			<Dialog.Description>
				Send a message to a tenant. Choose how it reaches them below.
			</Dialog.Description>
		</Dialog.Header>

		<div class="space-y-4" data-testid="message-compose-form">
			<!-- Recipient -->
			<div class="space-y-1">
				<span class="text-xs font-medium text-muted-foreground uppercase tracking-wide">To</span>
				<Select.Root type="single" bind:value={composeForm.tenantId}>
					<Select.Trigger class="w-full" data-testid="message-compose-tenant">
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

			<!-- Subject -->
			<div class="space-y-1">
				<label for="message-compose-subject" class="text-xs font-medium text-muted-foreground uppercase tracking-wide">Subject</label>
				<Input
					id="message-compose-subject"
					data-testid="message-compose-subject"
					bind:value={composeForm.subject}
					placeholder="What's this about?"
				/>
			</div>

			<!-- Body -->
			<div class="space-y-1">
				<label for="message-compose-body" class="text-xs font-medium text-muted-foreground uppercase tracking-wide">Message</label>
				<textarea
					id="message-compose-body"
					data-testid="message-compose-body"
					bind:value={composeForm.body}
					rows={5}
					class="w-full rounded border border-border bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
					placeholder="Type your message…"
				></textarea>
			</div>

			<!-- Channels -->
			<div class="space-y-2 rounded-lg border border-border bg-muted/30 p-3">
				<p class="text-xs font-medium text-muted-foreground uppercase tracking-wide">Send via</p>
				<label class="flex items-start gap-3">
					<Checkbox bind:checked={channels.portal} data-testid="message-channel-portal" />
					<span class="text-sm leading-tight">
						<span class="font-medium">Portal inbox</span>
						<span class="block text-xs text-muted-foreground">Shows up in the tenant's account.</span>
					</span>
				</label>
				<label class="flex items-start gap-3">
					<Checkbox bind:checked={channels.email} data-testid="message-channel-email" />
					<span class="text-sm leading-tight">
						<span class="font-medium">Email</span>
						<span class="block text-xs text-muted-foreground">Emails the tenant a copy.</span>
					</span>
				</label>
				<label class="flex items-start gap-3">
					<Checkbox bind:checked={channels.sms} data-testid="message-channel-sms" />
					<span class="text-sm leading-tight">
						<span class="font-medium">Text (SMS)</span>
						<span class="block text-xs text-muted-foreground">Texts the tenant. Requires Twilio setup.</span>
					</span>
				</label>
				{#if !anyChannel}
					<p class="text-xs text-destructive" data-testid="message-channel-error">Pick at least one way to send.</p>
				{/if}
			</div>
		</div>

		<Dialog.Footer>
			<Button variant="outline" onclick={closeCompose}>Cancel</Button>
			<Button
				data-testid="message-compose-send"
				onclick={sendCompose}
				disabled={!canSend || composeMutation.isPending}
			>
				{composeMutation.isPending ? 'Sending…' : 'Send message'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
