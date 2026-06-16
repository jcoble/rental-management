<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { notices } from '$lib/api/endpoints/notices';
	import { ai, type FairHousingReviewResult } from '$lib/api/endpoints/ai';
	import { ApiError, type FairHousingConcern } from '$lib/api/client';
	import type { NoticeDraft } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { formatDateOnly } from '$lib/utils/date';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import * as Card from '$lib/components/ui/card';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import FairHousingReviewDialog from '$lib/components/shared/FairHousingReviewDialog.svelte';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import { AlertTriangle, CheckCircle2, FileText, Mail, MessageSquare, MonitorSmartphone, RefreshCw, Send, ShieldCheck, Trash2 } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	let statusFilter = $state('Draft');
	const statusFilterOptions = [
		{ value: 'Draft', label: 'Draft' },
		{ value: 'Approved', label: 'Approved' },
		{ value: 'Dismissed', label: 'Dismissed' },
		{ value: '', label: 'All' }
	];
	const statusFilterLabel = $derived(
		statusFilterOptions.find((o) => o.value === statusFilter)?.label ?? 'All'
	);
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

	// Fair Housing review state for the copy currently being edited.
	let fhResult = $state<FairHousingReviewResult | null>(null);
	let fhChecking = $state(false);

	function resetFairHousing() {
		fhResult = null;
		fhChecking = false;
	}

	function startEdit(draft: NoticeDraft) {
		editingId = draft.id;
		editSubject = draft.subject;
		editBody = draft.body;
		resetFairHousing();
	}

	function cancelEdit() {
		editingId = null;
		resetFairHousing();
	}

	async function checkFairHousing() {
		const text = editBody.trim();
		if (text.length === 0) {
			showError('Add some message text before checking.');
			return;
		}
		fhChecking = true;
		fhResult = null;
		try {
			fhResult = await ai.fairHousingCheck(text);
		} catch (err) {
			showError(apiErrorMessage(err));
		} finally {
			fhChecking = false;
		}
	}

	function useSuggestedRewrite() {
		if (fhResult?.suggestedRewrite) {
			editBody = fhResult.suggestedRewrite;
			// Re-checking the rewritten copy is the user's call; clear the stale verdict.
			fhResult = null;
			showSuccess('Suggested rewrite applied. You can re-check it if you like.');
		}
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

	// Fair Housing review gate: a 422 from POST /notices/{id}/approve means the copy was flagged. We show
	// the concerns and let the landlord either Edit (revise) or send anyway (re-approve with the ack flag).
	let fhReviewOpen = $state(false);
	let fhDetail = $state('');
	let fhConcerns = $state<FairHousingConcern[]>([]);
	let fhPendingDraft = $state<NoticeDraft | null>(null);

	const approveMutation = createMutation(() => ({
		mutationFn: ({ draft, acknowledged = false }: { draft: NoticeDraft; acknowledged?: boolean }) =>
			notices.approve(draft.id, {
				channels: draftChannels(draft),
				...(acknowledged ? { acknowledgedFairHousingReview: true } : {})
			}),
		onSuccess: () => {
			fhReviewOpen = false;
			fhPendingDraft = null;
			showSuccess('Notice approved and sent.');
			invalidate();
		},
		onError: (err) => {
			if (err instanceof ApiError && err.fairHousingConcerns) {
				fhDetail = err.message;
				fhConcerns = err.fairHousingConcerns;
				fhReviewOpen = true;
				return;
			}
			showError(apiErrorMessage(err));
		}
	}));

	function approveDraft(draft: NoticeDraft) {
		fhPendingDraft = draft;
		approveMutation.mutate({ draft });
	}

	function editFairHousing() {
		// Dismiss the review so the landlord can revise the copy (Edit on the draft card).
		fhReviewOpen = false;
	}

	function sendNoticeAnyway() {
		if (!fhPendingDraft || approveMutation.isPending) return;
		approveMutation.mutate({ draft: fhPendingDraft, acknowledged: true });
	}

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

	// A subtle left-edge accent + icon tint per notice meaning: renewals read
	// positive (blue), late rent reads urgent (red), move-out reads warning (amber).
	function noticeAccent(type: string) {
		switch (type) {
			case 'RenewalOffer': return { bar: 'bg-primary', icon: 'text-primary' };
			case 'LateRentNotice': return { bar: 'bg-destructive', icon: 'text-destructive' };
			case 'MoveOutReminder': return { bar: 'bg-warning', icon: 'text-warning' };
			default: return { bar: 'bg-muted-foreground/40', icon: 'text-muted-foreground' };
		}
	}

</script>

<svelte:head>
	<title>Tenant notices - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="notices-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-4">
		<div>
			<h1 class="flex items-center gap-2 text-2xl font-bold">
				<FileText class="h-6 w-6 text-primary" />
				Tenant notices
			</h1>
			<p class="mt-1 text-sm text-muted-foreground">
				Lease lifecycle drafts for renewals, late rent, and move-out reminders. Review, edit, then approve to send.
			</p>
		</div>
		<div class="flex flex-wrap items-center gap-2">
			<Select.Root type="single" bind:value={statusFilter}>
				<Select.Trigger class="w-36" data-testid="notice-status-filter">
					{statusFilterLabel}
				</Select.Trigger>
				<Select.Content>
					{#each statusFilterOptions as option}
						<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<HelpPopover
				title="Generate drafts"
				summary="Scans your leases and auto-creates notice drafts — renewal, late-rent, and move-out reminders — that are due."
				detail="Nothing is sent automatically: each draft waits for you to review, edit, and approve before it goes out."
				learnMoreUrl={undefined}
			/>
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
				{@const accent = noticeAccent(draft.noticeType)}
				<Card.Root class="relative gap-0 overflow-hidden py-0" data-testid="notice-draft-{draft.id}">
					<span class="absolute inset-y-0 left-0 w-1 {accent.bar}" aria-hidden="true"></span>
					<Card.Header class="border-b border-border px-5 py-4">
						<div class="flex flex-wrap items-start justify-between gap-3">
							<div class="min-w-0">
								<div class="flex items-center gap-2">
									<FileText class="h-4 w-4 shrink-0 {accent.icon}" />
									<h3 class="text-lg font-semibold leading-tight" data-testid="notice-draft-title">
										{noticeLabel(draft.noticeType)} · {draft.tenantName}
									</h3>
								</div>
								<p class="mt-1 text-xs text-muted-foreground">
									{draft.propertyName ?? 'Property'}{draft.unitNumber ? ` · Unit ${draft.unitNumber}` : ''} · {draft.reason}
								</p>
							</div>
							<StatusBadge status={draft.status} />
						</div>
					</Card.Header>
					<Card.Content class="space-y-4 p-5">
						{#if editingId === draft.id}
							<div class="space-y-3">
								<input class="h-10 w-full rounded-md border border-input bg-background px-3 text-sm" bind:value={editSubject} data-testid="notice-edit-subject" />
								<textarea class="min-h-36 w-full rounded-md border border-input bg-background p-3 text-sm" bind:value={editBody} data-testid="notice-edit-body"></textarea>
								<div class="flex flex-wrap gap-2">
									<Button size="sm" onclick={() => updateMutation.mutate({ id: draft.id, subject: editSubject, body: editBody })} disabled={updateMutation.isPending}>
										Save draft
									</Button>
									<Button size="sm" variant="outline" onclick={checkFairHousing} disabled={fhChecking} data-testid="fair-housing-check">
										<ShieldCheck class="mr-1.5 h-4 w-4" />
										{fhChecking ? 'Checking...' : 'Check for fair-housing issues'}
									</Button>
									<HelpPopover
										title="Check for fair-housing issues"
										summary="Scans your notice wording for Fair-Housing-risky language and suggests a compliant rewrite."
										detail="It flags phrases that could imply discrimination against a protected class, with a reason for each. If AI is off it says so rather than passing it as clean."
										learnMoreUrl={undefined}
									/>
									<Button size="sm" variant="outline" onclick={cancelEdit}>Cancel</Button>
								</div>

								{#if fhResult}
									{#if !fhResult.reviewed}
										<div class="flex items-start gap-2 rounded-md border border-border bg-muted p-3 text-sm text-muted-foreground" data-testid="fair-housing-unavailable">
											<AlertTriangle class="mt-0.5 h-4 w-4 shrink-0" />
											<span>AI review unavailable (no AI key configured). The copy was not checked.</span>
										</div>
									{:else if fhResult.compliant}
										<div class="flex items-start gap-2 rounded-md border bg-[var(--m3c-success-container)] text-[var(--m3c-on-success-container)] border-[color-mix(in_srgb,var(--success)_45%,transparent)] p-3 text-sm" data-testid="fair-housing-compliant">
											<CheckCircle2 class="mt-0.5 h-4 w-4 shrink-0" />
											<span>Looks compliant. No fair-housing issues found.</span>
										</div>
									{:else}
										<div class="m3-warning-surface space-y-2 rounded-md p-3 text-sm" data-testid="fair-housing-issues">
											<p class="flex items-center gap-2 font-medium text-[var(--warning)]">
												<AlertTriangle class="h-4 w-4 shrink-0" />
												Possible fair-housing issues
											</p>
											<ul class="space-y-1.5">
												{#each fhResult.issues as issue (issue.phrase + issue.concern)}
													<li class="text-muted-foreground">
														<span class="font-medium text-foreground">&ldquo;{issue.phrase}&rdquo;</span> — {issue.concern}
													</li>
												{/each}
											</ul>
											{#if fhResult.suggestedRewrite}
												<div class="border-t border-[color-mix(in_srgb,var(--warning)_30%,transparent)] pt-2">
													<p class="text-xs text-muted-foreground">Suggested compliant rewrite:</p>
													<p class="mt-1 whitespace-pre-wrap text-sm text-foreground">{fhResult.suggestedRewrite}</p>
													<Button size="sm" variant="outline" class="mt-2" onclick={useSuggestedRewrite} data-testid="fair-housing-use-rewrite">
														Use suggested rewrite
													</Button>
												</div>
											{/if}
										</div>
									{/if}
								{/if}
							</div>
						{:else}
							<div>
								<p class="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Subject</p>
								<p class="mt-0.5 text-base font-semibold leading-snug text-foreground">{draft.subject}</p>
								<p class="mt-3 whitespace-pre-wrap text-sm leading-6 text-muted-foreground">{draft.body}</p>
								<p class="mt-3 text-xs text-muted-foreground/70">Triggered {formatDateOnly(draft.triggerDate)}</p>
							</div>
						{/if}

						{#if draft.status === 'Draft'}
							<div class="flex flex-wrap items-center justify-between gap-3 border-t border-border pt-4">
								<div class="rounded-lg border border-border bg-muted/30 px-3 py-2">
									<p class="mb-1.5 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Send via</p>
									<div class="flex flex-wrap items-center gap-x-4 gap-y-1.5 text-sm">
										<label class="flex cursor-pointer items-center gap-1.5">
											<Checkbox checked={approveChannels[draft.id]?.portal ?? true} onCheckedChange={(v) => setChannel(draft.id, 'portal', v === true)} />
											<MonitorSmartphone class="h-3.5 w-3.5 text-muted-foreground" />
											Portal
										</label>
										<label class="flex cursor-pointer items-center gap-1.5">
											<Checkbox checked={approveChannels[draft.id]?.email ?? true} onCheckedChange={(v) => setChannel(draft.id, 'email', v === true)} />
											<Mail class="h-3.5 w-3.5 text-muted-foreground" />
											Email
										</label>
										<label class="flex cursor-pointer items-center gap-1.5">
											<Checkbox checked={approveChannels[draft.id]?.sms ?? true} onCheckedChange={(v) => setChannel(draft.id, 'sms', v === true)} />
											<MessageSquare class="h-3.5 w-3.5 text-muted-foreground" />
											SMS
										</label>
									</div>
								</div>
								<div class="flex flex-wrap gap-2">
									<Button size="sm" variant="outline" onclick={() => startEdit(draft)}>Edit</Button>
									<Button size="sm" onclick={() => approveDraft(draft)} disabled={approveMutation.isPending || draftChannels(draft).length === 0}>
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

	<!-- Fair Housing review gate (422 on approve) -->
	<FairHousingReviewDialog
		open={fhReviewOpen}
		detail={fhDetail}
		concerns={fhConcerns}
		busy={approveMutation.isPending}
		testid="notice-fair-housing"
		onedit={editFairHousing}
		onsendanyway={sendNoticeAnyway}
	/>
</div>
