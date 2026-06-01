<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { messages } from '$lib/api/endpoints/messages';
	import type { Message } from '$lib/types';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Dialog from '$lib/components/ui/dialog';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';

	const queryClient = useQueryClient();

	const MSG_STATUSES = ['Open', 'InProgress', 'Resolved', 'Closed'];

	let statusFilter = $state('');
	let selectedMessage = $state<Message | null>(null);
	let replyText = $state('');

	const messagesQuery = createQuery(() => ({
		queryKey: ['messages', statusFilter],
		queryFn: () => messages.list(statusFilter || undefined),
	}));

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
