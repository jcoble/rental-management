<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { notices } from '$lib/api/endpoints/notices';
	import type { NoticeDraft } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import { FileText, RefreshCw, Send, Trash2 } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	let statusFilter = $state('Draft');
	let editingId = $state<number | null>(null);
	let editSubject = $state('');
	let editBody = $state('');
	let approveChannels = $state<Record<string, { portal: boolean; email: boolean; sms: boolean }>>({});

	const draftsQuery = createQuery(() => ({
		queryKey: ['notice-drafts', portfolioId, statusFilter],
		queryFn: () => notices.list(statusFilter || undefined),
		enabled: !!portfolioId
	}));

	const drafts = $derived((draftsQuery.data as NoticeDraft[] | undefined) ?? []);

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['notice-drafts', portfolioId] });
	}

	function draftChannels(draft: NoticeDraft) {
		const current = approveChannels[draft.id] ?? { portal: true, email: true, sms: true };
		return [
			current.portal ? 'Portal' : '',
			current.email ? 'Email' : '',
			current.sms ? 'Sms' : ''
		].filter(Boolean);
	}

	function setChannel(id: number, key: 'portal' | 'email' | 'sms', checked: boolean) {
		const current = approveChannels[id] ?? { portal: true, email: true, sms: true };
		approveChannels = { ...approveChannels, [id]: { ...current, [key]: checked } };
	}

	function startEdit(draft: NoticeDraft) {
		editingId = draft.id;
		editSubject = draft.subject;
		editBody = draft.body;
	}

	const generateMutation = createMutation(() => ({
		mutationFn: () => notices.generate(),
		onSuccess: (result) => {
			showSuccess(result.createdCount === 0 ? 'No new notice drafts needed.' : `Created ${result.createdCount} notice draft${result.createdCount === 1 ? '' : 's'}.`);
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const updateMutation = createMutation(() => ({
		mutationFn: ({ id, subject, body }: { id: number; subject: string; body: string }) =>
			notices.update(id, { subject, body }),
		onSuccess: () => {
			showSuccess('Draft updated.');
			editingId = null;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const approveMutation = createMutation(() => ({
		mutationFn: (draft: NoticeDraft) => notices.approve(draft.id, { channels: draftChannels(draft) }),
		onSuccess: () => {
			showSuccess('Notice approved and sent.');
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const dismissMutation = createMutation(() => ({
		mutationFn: (id: number) => notices.dismiss(id),
		onSuccess: () => {
			showSuccess('Draft dismissed.');
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function noticeLabel(type: string) {
		switch (type) {
			case 'RenewalOffer': return 'Renewal offer';
			case 'LateRentNotice': return 'Late rent';
			case 'MoveOutReminder': return 'Move-out';
			default: return type;
		}
	}

	function date(value: string) {
		return new Date(value).toLocaleDateString();
	}
</script>

<svelte:head>
	<title>Notices - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="notices-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-4">
		<div>
			<h1 class="flex items-center gap-2 text-2xl font-bold">
				<FileText class="h-6 w-6 text-primary" />
				Notices
			</h1>
			<p class="mt-1 text-sm text-muted-foreground">
				Lease lifecycle drafts for renewals, late rent, and move-out reminders. Review, edit, then approve to send.
			</p>
		</div>
		<div class="flex flex-wrap items-center gap-2">
			<select class="h-9 rounded-md border border-input bg-background px-3 text-sm" bind:value={statusFilter}>
				<option value="Draft">Draft</option>
				<option value="Approved">Approved</option>
				<option value="Dismissed">Dismissed</option>
				<option value="">All</option>
			</select>
			<Button onclick={() => generateMutation.mutate()} disabled={generateMutation.isPending} data-testid="generate-notices">
				<RefreshCw class="mr-1.5 h-4 w-4" />
				{generateMutation.isPending ? 'Generating...' : 'Generate drafts'}
			</Button>
		</div>
	</div>

	{#if draftsQuery.isLoading}
		<p class="py-12 text-center text-sm text-muted-foreground">Loading notice drafts...</p>
	{:else if draftsQuery.isError}
		<p class="py-12 text-center text-sm text-destructive">Could not load notice drafts.</p>
	{:else if drafts.length === 0}
		<Card.Root class="gap-0 py-0">
			<Card.Content class="p-8 text-center text-sm text-muted-foreground">
				No notice drafts in this view.
			</Card.Content>
		</Card.Root>
	{:else}
		<div class="grid gap-4" data-testid="notice-draft-list">
			{#each drafts as draft (draft.id)}
				<Card.Root class="gap-0 py-0" data-testid="notice-draft-{draft.id}">
					<Card.Header class="border-b border-border px-4 py-3">
						<div class="flex flex-wrap items-start justify-between gap-3">
							<div>
								<Card.Title class="text-base">{noticeLabel(draft.noticeType)} - {draft.tenantName}</Card.Title>
								<Card.Description>
									{draft.propertyName ?? 'Property'}{draft.unitNumber ? ` / Unit ${draft.unitNumber}` : ''} / {draft.reason}
								</Card.Description>
							</div>
							<span class="rounded-full bg-muted px-2 py-1 text-xs font-medium">{draft.status}</span>
						</div>
					</Card.Header>
					<Card.Content class="space-y-4 p-4">
						{#if editingId === draft.id}
							<div class="space-y-3">
								<input class="h-10 w-full rounded-md border border-input bg-background px-3 text-sm" bind:value={editSubject} />
								<textarea class="min-h-36 w-full rounded-md border border-input bg-background p-3 text-sm" bind:value={editBody}></textarea>
								<div class="flex gap-2">
									<Button size="sm" onclick={() => updateMutation.mutate({ id: draft.id, subject: editSubject, body: editBody })} disabled={updateMutation.isPending}>
										Save draft
									</Button>
									<Button size="sm" variant="outline" onclick={() => editingId = null}>Cancel</Button>
								</div>
							</div>
						{:else}
							<div>
								<p class="text-sm font-medium">{draft.subject}</p>
								<p class="mt-2 whitespace-pre-wrap text-sm leading-6 text-muted-foreground">{draft.body}</p>
								<p class="mt-3 text-xs text-muted-foreground">Trigger date: {date(draft.triggerDate)}</p>
							</div>
						{/if}

						{#if draft.status === 'Draft'}
							<div class="flex flex-wrap items-center justify-between gap-3 border-t border-border pt-3">
								<div class="flex flex-wrap items-center gap-3 text-sm">
									<label class="flex items-center gap-1.5">
										<input type="checkbox" checked={approveChannels[draft.id]?.portal ?? true} onchange={(e) => setChannel(draft.id, 'portal', e.currentTarget.checked)} />
										Portal
									</label>
									<label class="flex items-center gap-1.5">
										<input type="checkbox" checked={approveChannels[draft.id]?.email ?? true} onchange={(e) => setChannel(draft.id, 'email', e.currentTarget.checked)} />
										Email
									</label>
									<label class="flex items-center gap-1.5">
										<input type="checkbox" checked={approveChannels[draft.id]?.sms ?? true} onchange={(e) => setChannel(draft.id, 'sms', e.currentTarget.checked)} />
										SMS
									</label>
								</div>
								<div class="flex flex-wrap gap-2">
									<Button size="sm" variant="outline" onclick={() => startEdit(draft)}>Edit</Button>
									<Button size="sm" onclick={() => approveMutation.mutate(draft)} disabled={approveMutation.isPending || draftChannels(draft).length === 0}>
										<Send class="mr-1.5 h-4 w-4" />
										Approve & send
									</Button>
									<Button size="sm" variant="outline" onclick={() => dismissMutation.mutate(draft.id)} disabled={dismissMutation.isPending}>
										<Trash2 class="mr-1.5 h-4 w-4" />
										Dismiss
									</Button>
								</div>
							</div>
						{:else if draft.conversationId}
							<div class="border-t border-border pt-3">
								<Button size="sm" variant="outline" href={`/messages?conversation=${draft.conversationId}`}>
									Open conversation
								</Button>
							</div>
						{/if}
					</Card.Content>
				</Card.Root>
			{/each}
		</div>
	{/if}
</div>
