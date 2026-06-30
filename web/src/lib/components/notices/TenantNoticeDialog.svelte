<script lang="ts">
	import { createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { notices } from '$lib/api/endpoints/notices';
	import type { NoticeDraft } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { getTenantNoticeEmptyState } from '$lib/tenants/tenant-notice-state';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import { AlertCircle, BellRing, Send, Trash2 } from '@lucide/svelte';

	let {
		open = $bindable(false),
		tenantId,
		tenantName = 'this tenant',
		activeLeaseCount = 0,
		initialNoticeType,
	}: {
		open: boolean;
		tenantId: number;
		tenantName?: string;
		activeLeaseCount?: number;
		initialNoticeType?: string;
	} = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	let noticeDrafts = $state<NoticeDraft[]>([]);
	let forcedNoticeLabel = $state<string | null>(null);
	let noticeChannels = $state<Record<number, { portal: boolean; email: boolean; sms: boolean }>>({});
	let noticeEdits = $state<Record<number, { subject: string; body: string }>>({});
	let lastOpenKey: string | null = null;

	const noticeEmptyState = $derived(
		getTenantNoticeEmptyState({
			forcedNoticeLabel,
			activeLeaseCount,
			tenantId,
		})
	);

	const FORCEABLE_NOTICE_TYPES: { type: string; label: string }[] = [
		{ type: 'RentReminder', label: 'Rent reminder (coming due)' },
		{ type: 'RenewalOffer', label: 'Lease renewal offer' },
		{ type: 'MonthToMonthConversion', label: 'Convert to month-to-month' },
		{ type: 'MoveOutReminder', label: 'Lease expiration / move-out' },
		{ type: 'LateRentNotice', label: 'Late rent / late fee' },
	];

	function noticeTypeLabel(type: string) {
		switch (type) {
			case 'RentReminder': return 'Rent reminder';
			case 'RenewalOffer': return 'Renewal offer';
			case 'MonthToMonthConversion': return 'Month-to-month conversion';
			case 'MoveOutReminder': return 'Move-out reminder';
			case 'LateRentNotice': return 'Late rent / late fee';
			default: return type;
		}
	}

	function channelsFor(id: number) {
		return noticeChannels[id] ?? { portal: true, email: true, sms: true };
	}

	function setNoticeChannel(id: number, key: 'portal' | 'email' | 'sms', checked: boolean) {
		noticeChannels = { ...noticeChannels, [id]: { ...channelsFor(id), [key]: checked } };
	}

	function selectedChannelNames(id: number): string[] {
		const c = channelsFor(id);
		return [c.portal ? 'Portal' : '', c.email ? 'Email' : '', c.sms ? 'Sms' : ''].filter(Boolean);
	}

	function noticeEditFor(draft: NoticeDraft) {
		return noticeEdits[draft.id] ?? { subject: draft.subject, body: draft.body };
	}

	function setNoticeEdit(id: number, field: 'subject' | 'body', value: string) {
		noticeEdits = {
			...noticeEdits,
			[id]: { ...(noticeEdits[id] ?? { subject: '', body: '' }), [field]: value },
		};
	}

	function editedNoticePayload(draft: NoticeDraft) {
		const edit = noticeEditFor(draft);
		return {
			subject: edit.subject.trim(),
			body: edit.body.trim(),
		};
	}

	function hasEditedNoticeContent(draft: NoticeDraft) {
		const edit = editedNoticePayload(draft);
		return edit.subject.length > 0 && edit.body.length > 0;
	}

	function removeNoticeEdit(id: number) {
		const { [id]: _removed, ...next } = noticeEdits;
		noticeEdits = next;
	}

	function resetNoticeState() {
		noticeDrafts = [];
		noticeChannels = {};
		noticeEdits = {};
		forcedNoticeLabel = null;
	}

	function generateNoticeDrafts(noticeType?: string) {
		resetNoticeState();
		generateNoticeMutation.mutate(noticeType);
	}

	$effect(() => {
		if (!open || tenantId <= 0) {
			lastOpenKey = null;
			return;
		}

		const openKey = `${tenantId}:${initialNoticeType ?? 'due'}`;
		if (lastOpenKey === openKey) return;
		lastOpenKey = openKey;
		generateNoticeDrafts(initialNoticeType);
	});

	const generateNoticeMutation = createMutation(() => ({
		mutationFn: (noticeType?: string) => notices.generate(tenantId, noticeType),
		onSuccess: (result, noticeType) => {
			const selectedForcedNoticeLabel = noticeType
				? FORCEABLE_NOTICE_TYPES.find((nt) => nt.type === noticeType)?.label ?? noticeTypeLabel(noticeType)
				: null;
			forcedNoticeLabel = selectedForcedNoticeLabel;
			noticeDrafts = result.drafts ?? [];
			const seeded: Record<number, { portal: boolean; email: boolean; sms: boolean }> = {};
			const seededEdits: Record<number, { subject: string; body: string }> = {};
			for (const d of noticeDrafts) {
				seeded[d.id] = { portal: true, email: true, sms: true };
				seededEdits[d.id] = { subject: d.subject, body: d.body };
			}
			noticeChannels = seeded;
			noticeEdits = seededEdits;
			queryClient.invalidateQueries({ queryKey: ['notice-drafts', portfolioId] });
			if (noticeDrafts.length === 0) {
				showSuccess(
					getTenantNoticeEmptyState({
						forcedNoticeLabel: selectedForcedNoticeLabel,
						activeLeaseCount,
						tenantId,
					}).message
				);
			}
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const sendNoticeMutation = createMutation(() => ({
		mutationFn: async (draft: NoticeDraft) => {
			const payload = editedNoticePayload(draft);
			const changed =
				payload.subject !== draft.subject.trim() || payload.body !== draft.body.trim();
			if (changed) await notices.update(draft.id, payload);
			return notices.approve(draft.id, { channels: selectedChannelNames(draft.id) });
		},
		onSuccess: (_r, draft) => {
			showSuccess('Notice sent.');
			noticeDrafts = noticeDrafts.filter((d) => d.id !== draft.id);
			removeNoticeEdit(draft.id);
			queryClient.invalidateQueries({ queryKey: ['notice-drafts', portfolioId] });
			if (noticeDrafts.length === 0) open = false;
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const dismissNoticeMutation = createMutation(() => ({
		mutationFn: (draft: NoticeDraft) => notices.dismiss(draft.id),
		onSuccess: (_r, draft) => {
			showSuccess('Draft dismissed.');
			noticeDrafts = noticeDrafts.filter((d) => d.id !== draft.id);
			removeNoticeEdit(draft.id);
			queryClient.invalidateQueries({ queryKey: ['notice-drafts', portfolioId] });
			if (noticeDrafts.length === 0) open = false;
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));
</script>

<Dialog.Root {open} onOpenChange={(v) => { if (!v) open = false; }}>
	<Dialog.Content class="max-h-[85vh] max-w-2xl overflow-y-auto" data-testid="tenant-notice-dialog">
		<Dialog.Header>
			<Dialog.Title>Create / Send notice</Dialog.Title>
			<Dialog.Description>
				Notices due for {tenantName || 'this tenant'} — review, choose how to deliver each, then send.
				Nothing goes out until you press Send.
			</Dialog.Description>
		</Dialog.Header>

		{#if generateNoticeMutation.isPending}
			<div class="flex items-center justify-center gap-2 py-10 text-muted-foreground" data-testid="tenant-notice-loading">
				<div class="h-5 w-5 animate-spin rounded-full border-2 border-current border-t-transparent"></div>
				<span class="text-sm">Generating notice drafts…</span>
			</div>
		{:else if generateNoticeMutation.isError}
			<div class="rounded-lg border border-destructive/30 bg-destructive/10 p-4 text-center" data-testid="tenant-notice-error">
				<AlertCircle class="mx-auto mb-2 h-6 w-6 text-destructive" />
				<p class="text-sm text-destructive">{apiErrorMessage(generateNoticeMutation.error)}</p>
				<Button variant="outline" class="mt-3" onclick={() => generateNoticeDrafts(initialNoticeType)} data-testid="tenant-notice-retry">
					Try again
				</Button>
			</div>
		{:else if noticeDrafts.length === 0}
			<div class="rounded-lg border border-border p-6 text-center" data-testid="tenant-notice-empty">
				<BellRing class="mx-auto mb-2 h-6 w-6 text-muted-foreground" />
				<p class="text-sm font-medium">{noticeEmptyState.message}</p>
				<p class="mt-1 text-xs text-muted-foreground">
					{noticeEmptyState.description}
				</p>
				{#if noticeEmptyState.showForceControls}
					<p class="mt-4 text-xs font-medium uppercase tracking-wide text-muted-foreground">Create one anyway</p>
					<div class="mt-2 flex flex-wrap items-center justify-center gap-2">
						{#each FORCEABLE_NOTICE_TYPES as nt (nt.type)}
							<Button
								variant="outline"
								size="sm"
								disabled={generateNoticeMutation.isPending}
								onclick={() => generateNoticeDrafts(nt.type)}
								data-testid="tenant-notice-force-{nt.type}"
							>
								{nt.label}
							</Button>
						{/each}
					</div>
				{:else if noticeEmptyState.leaseActionHref}
					<Button
						variant="outline"
						size="sm"
						class="mt-4"
						href={noticeEmptyState.leaseActionHref}
						data-testid="tenant-notice-create-lease"
					>
						{noticeEmptyState.leaseActionLabel}
					</Button>
				{/if}
			</div>
		{:else}
			<div class="space-y-4" data-testid="tenant-notice-drafts">
				{#each noticeDrafts as draft (draft.id)}
					{@const channels = channelsFor(draft.id)}
					{@const busy = sendNoticeMutation.isPending || dismissNoticeMutation.isPending}
					{@const canSend = channels.portal || channels.email || channels.sms}
					{@const edit = noticeEditFor(draft)}
					<div class="rounded-lg border border-border bg-card p-4" data-testid="tenant-notice-draft-{draft.id}">
						<div class="mb-2 flex items-center justify-between gap-2">
							<h3 class="text-sm font-semibold" data-testid="tenant-notice-draft-type">
								{noticeTypeLabel(draft.noticeType)}
							</h3>
							<StatusBadge status={draft.status} />
						</div>
						<div class="space-y-3">
							<label class="block">
								<span class="mb-1 block text-xs font-medium uppercase tracking-wide text-muted-foreground">Subject</span>
								<input
									class="h-10 w-full rounded-md border border-input bg-background px-3 text-sm"
									value={edit.subject}
									oninput={(event) => setNoticeEdit(draft.id, 'subject', (event.currentTarget as HTMLInputElement).value)}
									data-testid="tenant-notice-edit-subject-{draft.id}"
								/>
							</label>
							<label class="block">
								<span class="mb-1 block text-xs font-medium uppercase tracking-wide text-muted-foreground">Message</span>
								<textarea
									class="min-h-32 w-full rounded-md border border-input bg-background p-3 text-sm leading-6"
									value={edit.body}
									oninput={(event) => setNoticeEdit(draft.id, 'body', (event.currentTarget as HTMLTextAreaElement).value)}
									data-testid="tenant-notice-edit-body-{draft.id}"
								></textarea>
							</label>
						</div>

						<div class="mt-3 flex flex-wrap items-center gap-4">
							<span class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Send via</span>
							<label class="flex items-center gap-2 text-sm" data-testid="tenant-notice-channel-portal-{draft.id}">
								<Checkbox checked={channels.portal} onCheckedChange={(v) => setNoticeChannel(draft.id, 'portal', v === true)} />
								Portal
							</label>
							<label class="flex items-center gap-2 text-sm" data-testid="tenant-notice-channel-email-{draft.id}">
								<Checkbox checked={channels.email} onCheckedChange={(v) => setNoticeChannel(draft.id, 'email', v === true)} />
								Email
							</label>
							<label class="flex items-center gap-2 text-sm" data-testid="tenant-notice-channel-sms-{draft.id}">
								<Checkbox checked={channels.sms} onCheckedChange={(v) => setNoticeChannel(draft.id, 'sms', v === true)} />
								SMS
							</label>
						</div>

						<div class="mt-4 flex items-center justify-end gap-2">
							<Button variant="ghost" size="sm" disabled={busy} onclick={() => dismissNoticeMutation.mutate(draft)} data-testid="tenant-notice-dismiss-{draft.id}">
								<Trash2 class="h-4 w-4" />
								Dismiss
							</Button>
							<Button size="sm" class="gap-2" disabled={busy || !canSend || !hasEditedNoticeContent(draft)} onclick={() => sendNoticeMutation.mutate(draft)} data-testid="tenant-notice-send-{draft.id}">
								<Send class="h-4 w-4" />
								Send
							</Button>
						</div>
					</div>
				{/each}
			</div>
		{/if}

		<Dialog.Footer>
			<Button variant="outline" onclick={() => (open = false)} data-testid="tenant-notice-close">
				Close
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
