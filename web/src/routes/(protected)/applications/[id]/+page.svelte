<script lang="ts">
	import { page } from '$app/stores';
	import { goto } from '$app/navigation';
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import {
		applications,
		type ApplicationResponse,
		type ScreeningResultResponse,
		type AdverseActionNoticeResponse,
	} from '$lib/api/endpoints/applications';
	import { downloadDocument } from '$lib/api/endpoints/documents';
	import { ApiError } from '$lib/api/client';
	import { showSuccess, showWarning, showError, apiErrorMessage } from '$lib/utils/toast';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import {
		Mail,
		Phone,
		AlertCircle,
		FileText,
		CheckCircle2,
		XCircle,
		Ban,
		ArrowRight,
		Calendar,
		Home,
		Briefcase,
		ShieldCheck,
		ScanSearch,
		Download,
		User,
	} from '@lucide/svelte';

	const queryClient = useQueryClient();
	const id = $derived(Number($page.params.id));

	const applicationQuery = createQuery(() => ({
		queryKey: ['application', id],
		queryFn: () => applications.get(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const application = $derived<ApplicationResponse | undefined>(applicationQuery.data);
	const fullName = $derived(application ? `${application.firstName} ${application.lastName}` : '');
	const isOpen = $derived(
		application?.status === 'Submitted' || application?.status === 'UnderReview'
	);

	const STATUS_MAP = {
		Submitted: { label: 'Submitted', class: 'bg-blue-100 text-blue-800 border-blue-200 dark:bg-blue-900/30 dark:text-blue-400 dark:border-blue-800' },
		UnderReview: { label: 'Under Review', class: 'bg-purple-100 text-purple-800 border-purple-200 dark:bg-purple-900/30 dark:text-purple-400 dark:border-purple-800' },
		Declined: { label: 'Declined', class: 'bg-red-100 text-red-800 border-red-200 dark:bg-red-900/30 dark:text-red-400 dark:border-red-800' },
		Withdrawn: { label: 'Withdrawn', class: 'bg-muted text-muted-foreground border-border' },
	};

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['application', id] });
		queryClient.invalidateQueries({ queryKey: ['applications'] });
	}

	// ── Approve ───────────────────────────────────────────────────────────────────
	let showApprove = $state(false);
	let approvedTenantId = $state<number | null>(null);

	const approveMutation = createMutation(() => ({
		mutationFn: () => applications.approve(id),
		onSuccess: (result) => {
			approvedTenantId = result.tenantId;
			showApprove = false;
			showSuccess('Application approved. A tenant record was created.');
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// ── Decline ─────────────────────────────────────────────────────────────────
	let showDecline = $state(false);
	let declineReason = $state('');

	const declineMutation = createMutation(() => ({
		mutationFn: (reason: string) => applications.decline(id, reason.trim() || undefined),
		onSuccess: () => {
			showDecline = false;
			declineReason = '';
			showSuccess('Application declined.');
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// ── Withdraw ─────────────────────────────────────────────────────────────────
	let showWithdraw = $state(false);

	const withdrawMutation = createMutation(() => ({
		mutationFn: () => applications.withdraw(id),
		onSuccess: () => {
			showWithdraw = false;
			showSuccess('Application withdrawn.');
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// ── Screening ─────────────────────────────────────────────────────────────────
	const screeningQuery = createQuery(() => ({
		queryKey: ['application-screening', id],
		queryFn: () => applications.screening(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const latestScreening = $derived<ScreeningResultResponse | undefined>(
		screeningQuery.data?.[0]
	);
	const hasScreening = $derived(!!latestScreening);
	const canScreen = $derived(application?.consentGiven === true);

	const RECOMMENDATION_MAP = {
		Accept: { label: 'Accept', class: 'bg-green-100 text-green-800 border-green-200 dark:bg-green-900/30 dark:text-green-400 dark:border-green-800' },
		Conditional: { label: 'Conditional', class: 'bg-yellow-100 text-yellow-800 border-yellow-200 dark:bg-yellow-900/30 dark:text-yellow-500 dark:border-yellow-800' },
		Decline: { label: 'Decline', class: 'bg-red-100 text-red-800 border-red-200 dark:bg-red-900/30 dark:text-red-400 dark:border-red-800' },
	};
	const SCREEN_STATUS_MAP = {
		Requested: { label: 'Requested', class: 'bg-blue-100 text-blue-800 border-blue-200 dark:bg-blue-900/30 dark:text-blue-400 dark:border-blue-800' },
		Completed: { label: 'Completed', class: 'bg-green-100 text-green-800 border-green-200 dark:bg-green-900/30 dark:text-green-400 dark:border-green-800' },
		Failed: { label: 'Failed', class: 'bg-red-100 text-red-800 border-red-200 dark:bg-red-900/30 dark:text-red-400 dark:border-red-800' },
	};

	const screenMutation = createMutation(() => ({
		mutationFn: () => applications.screen(id),
		onSuccess: () => {
			showSuccess('Screening complete.');
			queryClient.invalidateQueries({ queryKey: ['application-screening', id] });
			invalidate(); // screening flips the application to UnderReview
		},
		onError: (err) => {
			if (err instanceof ApiError && err.status === 503) {
				showWarning("Screening isn't set up yet (no screening provider configured).");
				return;
			}
			showError(apiErrorMessage(err));
		},
	}));

	// ── Adverse-action notice ──────────────────────────────────────────────────────
	let showAdverseAction = $state(false);
	let adverseReason = $state('');
	let adverseSendToApplicant = $state(false);
	let adverseNotice = $state<AdverseActionNoticeResponse | null>(null);

	function openAdverseAction() {
		adverseReason = application?.decisionReason ?? '';
		adverseSendToApplicant = false;
		showAdverseAction = true;
	}

	const adverseActionMutation = createMutation(() => ({
		mutationFn: () =>
			applications.adverseAction(id, {
				reason: adverseReason.trim() || undefined,
				sendToApplicant: adverseSendToApplicant,
			}),
		onSuccess: (notice) => {
			adverseNotice = notice;
			showAdverseAction = false;
			showSuccess(
				notice.sentAtUtc
					? 'Adverse-action notice generated and sent to the applicant.'
					: 'Adverse-action notice generated.'
			);
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	let downloadingNotice = $state(false);
	async function downloadNotice() {
		if (!adverseNotice) return;
		downloadingNotice = true;
		try {
			await downloadDocument(adverseNotice.storedFileId, `adverse-action-notice-${id}.pdf`);
		} catch (err) {
			showError(apiErrorMessage(err, 'Could not download the notice.'));
		} finally {
			downloadingNotice = false;
		}
	}

	// The tenant link comes from a fresh approval this session, or a prior approval.
	const tenantLinkId = $derived(approvedTenantId ?? application?.approvedTenantId ?? null);

	function fmtDate(value: string | null | undefined): string {
		if (!value) return '—';
		const d = new Date(value);
		return isNaN(d.getTime()) ? '—' : d.toLocaleDateString(undefined, { timeZone: 'UTC' });
	}
	function fmtDateTime(value: string | null | undefined): string {
		if (!value) return '—';
		const d = new Date(value);
		return isNaN(d.getTime()) ? '—' : d.toLocaleString();
	}
	function fmtMoney(value: number | null | undefined): string {
		if (value == null) return '—';
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value);
	}
</script>

<svelte:head>
	<title>{fullName ? `${fullName} - Application` : 'Application'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="application-detail-page">
	<div class="mb-4">
		<PageBreadcrumb
			crumbs={[
				{ label: 'Applications', href: '/applications' },
				{ label: fullName || '…' },
			]}
		/>
	</div>

	{#if applicationQuery.isLoading}
		<div class="flex items-center justify-center py-20 text-muted-foreground" data-testid="application-detail-loading">
			<div class="flex flex-col items-center gap-2">
				<div class="h-6 w-6 animate-spin rounded-full border-2 border-current border-t-transparent"></div>
				<span class="text-sm">Loading…</span>
			</div>
		</div>
	{:else if applicationQuery.isError}
		<div class="rounded-lg border border-destructive/30 bg-destructive/10 p-6 text-center" data-testid="application-detail-error">
			<AlertCircle class="mx-auto mb-2 h-8 w-8 text-destructive" />
			<p class="font-medium text-destructive">Could not load application</p>
			<p class="mt-1 text-sm text-muted-foreground">{apiErrorMessage(applicationQuery.error)}</p>
			<Button variant="outline" class="mt-4" onclick={() => applicationQuery.refetch()}>Retry</Button>
		</div>
	{:else if !application}
		<div class="rounded-lg border border-border p-6 text-center" data-testid="application-detail-not-found">
			<FileText class="mx-auto mb-2 h-8 w-8 text-muted-foreground" />
			<p class="font-medium">Application not found</p>
			<Button variant="outline" class="mt-4" onclick={() => goto('/applications')}>Back to Applications</Button>
		</div>
	{:else}
		<!-- Header -->
		<div class="mb-6 flex flex-wrap items-start justify-between gap-3">
			<div>
				<div class="flex items-center gap-3">
					<h1 class="text-2xl font-bold" data-testid="application-detail-name">{fullName}</h1>
					<StatusBadge status={application.status} map={STATUS_MAP} />
				</div>
				<div class="mt-1 flex flex-wrap items-center gap-3 text-sm text-muted-foreground">
					<span class="flex items-center gap-1"><Mail class="h-3.5 w-3.5" />{application.email}</span>
					<span class="flex items-center gap-1"><Phone class="h-3.5 w-3.5" />{application.phone}</span>
				</div>
			</div>
			{#if isOpen}
				<div class="flex flex-wrap items-center gap-2">
					<Button class="gap-2" onclick={() => (showApprove = true)} data-testid="application-approve">
						<CheckCircle2 class="h-4 w-4" /> Approve
					</Button>
					<Button variant="outline" class="gap-2" onclick={() => (showDecline = true)} data-testid="application-decline">
						<XCircle class="h-4 w-4" /> Decline
					</Button>
					<Button variant="outline" class="gap-2" onclick={() => (showWithdraw = true)} data-testid="application-withdraw">
						<Ban class="h-4 w-4" /> Withdraw
					</Button>
				</div>
			{/if}
		</div>

		<!-- Approved → tenant link banner -->
		{#if tenantLinkId}
			<div
				class="mb-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-green-200 bg-green-50 p-4 dark:border-green-800 dark:bg-green-900/20"
				data-testid="application-tenant-banner"
			>
				<div class="flex items-center gap-2 text-sm text-green-800 dark:text-green-300">
					<CheckCircle2 class="h-5 w-5" />
					<span>This applicant was approved and a tenant record was created.</span>
				</div>
				<Button variant="outline" class="gap-2" onclick={() => goto(`/tenants/${tenantLinkId}`)} data-testid="application-view-tenant">
					<User class="h-4 w-4" /> View tenant <ArrowRight class="h-4 w-4" />
				</Button>
			</div>
		{/if}

		<!-- Declined reason -->
		{#if application.status === 'Declined' && application.decisionReason}
			<div class="mb-6 rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-800 dark:border-red-800 dark:bg-red-900/20 dark:text-red-300" data-testid="application-decline-reason">
				<span class="font-medium">Reason declined:</span> {application.decisionReason}
			</div>
		{/if}

		<div class="grid gap-6 lg:grid-cols-2">
			<!-- Applicant details -->
			<Card.Root>
				<Card.Header>
					<Card.Title class="flex items-center gap-2 text-base"><User class="h-4 w-4" /> Applicant</Card.Title>
				</Card.Header>
				<Card.Content class="grid grid-cols-2 gap-x-4 gap-y-4 text-sm" data-testid="application-applicant-fields">
					{@render fieldRow('First name', application.firstName)}
					{@render fieldRow('Last name', application.lastName)}
					{@render fieldRow('Email', application.email)}
					{@render fieldRow('Phone', application.phone)}
					{@render fieldRow('Date of birth', fmtDate(application.dateOfBirth))}
					{@render fieldRow('Desired move-in', fmtDate(application.desiredMoveInDate))}
				</Card.Content>
			</Card.Root>

			<!-- Residence & employment -->
			<Card.Root>
				<Card.Header>
					<Card.Title class="flex items-center gap-2 text-base"><Briefcase class="h-4 w-4" /> Residence & income</Card.Title>
				</Card.Header>
				<Card.Content class="grid grid-cols-2 gap-x-4 gap-y-4 text-sm">
					<div class="col-span-2">{@render fieldRow('Current address', application.currentAddress || '—')}</div>
					{@render fieldRow('Employer', application.employer || '—')}
					{@render fieldRow('Monthly income', fmtMoney(application.monthlyIncome))}
				</Card.Content>
			</Card.Root>

			<!-- Where -->
			<Card.Root>
				<Card.Header>
					<Card.Title class="flex items-center gap-2 text-base"><Home class="h-4 w-4" /> Requested home</Card.Title>
				</Card.Header>
				<Card.Content class="grid grid-cols-2 gap-x-4 gap-y-4 text-sm">
					{@render fieldRow('Property', application.propertyId != null ? `#${application.propertyId}` : 'No preference')}
					{@render fieldRow('Unit', application.unitId != null ? `#${application.unitId}` : 'No preference')}
				</Card.Content>
			</Card.Root>

			<!-- Consent & timeline -->
			<Card.Root>
				<Card.Header>
					<Card.Title class="flex items-center gap-2 text-base"><ShieldCheck class="h-4 w-4" /> Consent & timeline</Card.Title>
				</Card.Header>
				<Card.Content class="grid grid-cols-2 gap-x-4 gap-y-4 text-sm">
					<div class="col-span-2 flex items-center gap-2">
						{#if application.consentGiven}
							<CheckCircle2 class="h-4 w-4 text-green-600 dark:text-green-400" />
							<span data-testid="application-consent">
								Consent given{application.consentAtUtc ? ` on ${fmtDateTime(application.consentAtUtc)}` : ''}
							</span>
						{:else}
							<AlertCircle class="h-4 w-4 text-destructive" />
							<span class="text-destructive" data-testid="application-consent">No consent on record</span>
						{/if}
					</div>
					{@render fieldRow('Submitted', fmtDateTime(application.submittedAtUtc))}
					{@render fieldRow('Reviewed', fmtDateTime(application.reviewedAtUtc))}
				</Card.Content>
			</Card.Root>

			<!-- Notes -->
			{#if application.notes}
				<Card.Root class="lg:col-span-2">
					<Card.Header>
						<Card.Title class="flex items-center gap-2 text-base"><Calendar class="h-4 w-4" /> Notes from applicant</Card.Title>
					</Card.Header>
					<Card.Content>
						<p class="whitespace-pre-wrap text-sm text-foreground" data-testid="application-notes">{application.notes}</p>
					</Card.Content>
				</Card.Root>
			{/if}

			<!-- Screening -->
			<Card.Root class="lg:col-span-2" data-testid="application-screening-card">
				<Card.Header>
					<div class="flex flex-wrap items-center justify-between gap-3">
						<Card.Title class="flex items-center gap-2 text-base"><ScanSearch class="h-4 w-4" /> Screening</Card.Title>
						<div class="flex flex-col items-end gap-1">
							<Button
								class="gap-2"
								disabled={!canScreen || screenMutation.isPending}
								onclick={() => screenMutation.mutate()}
								data-testid="application-run-screening"
							>
								<ScanSearch class="h-4 w-4" />
								{screenMutation.isPending ? 'Screening…' : hasScreening ? 'Re-run screening' : 'Run screening'}
							</Button>
							{#if !canScreen}
								<p class="text-xs text-muted-foreground" data-testid="application-screening-consent-note">
									Applicant consent is required to screen
								</p>
							{/if}
						</div>
					</div>
				</Card.Header>
				<Card.Content>
					{#if screeningQuery.isLoading}
						<div class="flex items-center gap-2 text-sm text-muted-foreground">
							<div class="h-4 w-4 animate-spin rounded-full border-2 border-current border-t-transparent"></div>
							<span>Loading screening…</span>
						</div>
					{:else if latestScreening}
						<div class="space-y-4" data-testid="application-screening-result">
							<div class="flex flex-wrap items-center gap-2">
								<StatusBadge status={latestScreening.status} map={SCREEN_STATUS_MAP} />
								{#if latestScreening.recommendation}
									<StatusBadge
										status={latestScreening.recommendation}
										map={RECOMMENDATION_MAP}
									/>
								{/if}
								<span class="text-xs text-muted-foreground">
									Screened {fmtDateTime(latestScreening.completedAtUtc ?? latestScreening.requestedAtUtc)}
								</span>
							</div>
							<div class="grid grid-cols-2 gap-x-4 gap-y-4 text-sm sm:grid-cols-3">
								{@render fieldRow('Credit score band', latestScreening.creditScoreBand || '—')}
								<div class="min-w-0">
									<p class="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Criminal records</p>
									<p
										class="mt-0.5 flex items-center gap-1.5 text-sm {latestScreening.hasCriminalRecord ? 'text-red-700 dark:text-red-400' : 'text-foreground'}"
										data-testid="application-screening-criminal"
									>
										{#if latestScreening.hasCriminalRecord}
											<AlertCircle class="h-4 w-4 shrink-0" /> Records found — review
										{:else}
											<CheckCircle2 class="h-4 w-4 shrink-0 text-green-600 dark:text-green-400" /> No criminal records found
										{/if}
									</p>
								</div>
								<div class="min-w-0">
									<p class="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Eviction records</p>
									<p
										class="mt-0.5 flex items-center gap-1.5 text-sm {latestScreening.hasEvictionRecord ? 'text-red-700 dark:text-red-400' : 'text-foreground'}"
										data-testid="application-screening-eviction"
									>
										{#if latestScreening.hasEvictionRecord}
											<AlertCircle class="h-4 w-4 shrink-0" /> Records found — review
										{:else}
											<CheckCircle2 class="h-4 w-4 shrink-0 text-green-600 dark:text-green-400" /> No eviction records found
										{/if}
									</p>
								</div>
							</div>
						</div>
					{:else}
						<p class="text-sm text-muted-foreground" data-testid="application-screening-empty">
							No screening has been run yet.
						</p>
					{/if}
				</Card.Content>

				<!-- Adverse-action: shown once declined (or about to be) and a screening exists -->
				{#if hasScreening && application.status === 'Declined'}
					<Card.Footer class="flex-col items-stretch gap-3 border-t pt-4">
						<p class="text-xs text-muted-foreground" data-testid="application-adverse-action-help">
							When you decline based on a report, the law requires sending the applicant this notice.
						</p>
						{#if adverseNotice}
							<div
								class="rounded-lg border border-border bg-muted/40 p-4 text-sm"
								data-testid="application-adverse-action-notice"
							>
								<div class="flex flex-wrap items-center justify-between gap-3">
									<div class="space-y-1">
										<p class="font-medium text-foreground">Adverse-action notice generated</p>
										<p class="text-xs text-muted-foreground">
											Generated {fmtDateTime(adverseNotice.generatedAtUtc)}
											{#if adverseNotice.creditReportingAgency}
												· CRA: {adverseNotice.creditReportingAgency}
											{/if}
											{#if adverseNotice.sentAtUtc}
												· Sent to applicant {fmtDateTime(adverseNotice.sentAtUtc)}
											{/if}
										</p>
									</div>
									<Button
										variant="outline"
										class="gap-2"
										disabled={downloadingNotice}
										onclick={downloadNotice}
										data-testid="application-adverse-action-download"
									>
										<Download class="h-4 w-4" />
										{downloadingNotice ? 'Downloading…' : 'Download notice (PDF)'}
									</Button>
								</div>
							</div>
						{:else}
							<div>
								<Button
									variant="outline"
									class="gap-2"
									onclick={openAdverseAction}
									data-testid="application-adverse-action-open"
								>
									<FileText class="h-4 w-4" /> Generate adverse-action notice
								</Button>
							</div>
						{/if}
					</Card.Footer>
				{/if}
			</Card.Root>
		</div>
	{/if}
</div>

{#snippet fieldRow(label: string, value: string)}
	<div class="min-w-0">
		<p class="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{label}</p>
		<p class="mt-0.5 break-words text-sm text-foreground">{value}</p>
	</div>
{/snippet}

<!-- Approve confirmation -->
<ConfirmDialog
	open={showApprove}
	title="Approve application"
	message={`Approve ${fullName}? This creates a tenant record you can then place on a lease.`}
	confirmLabel="Approve"
	busy={approveMutation.isPending}
	testid="application-approve-confirm"
	onconfirm={() => approveMutation.mutate()}
	oncancel={() => (showApprove = false)}
/>

<!-- Withdraw confirmation -->
<ConfirmDialog
	open={showWithdraw}
	title="Withdraw application"
	message={`Withdraw ${fullName}'s application? This marks it as withdrawn.`}
	confirmLabel="Withdraw"
	busy={withdrawMutation.isPending}
	testid="application-withdraw-confirm"
	onconfirm={() => withdrawMutation.mutate()}
	oncancel={() => (showWithdraw = false)}
/>

<!-- Decline with reason -->
<Dialog.Root open={showDecline} onOpenChange={(v) => { if (!v) showDecline = false; }}>
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>Decline application</Dialog.Title>
			<Dialog.Description>Optionally add a reason. This is kept for your records.</Dialog.Description>
		</Dialog.Header>
		<textarea
			bind:value={declineReason}
			rows="3"
			placeholder="Reason (optional)…"
			class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground focus:border-ring focus:outline-none focus:ring-2 focus:ring-ring/40"
			data-testid="application-decline-reason-input"
		></textarea>
		<Dialog.Footer>
			<Button variant="outline" onclick={() => (showDecline = false)} data-testid="application-decline-cancel">Cancel</Button>
			<Button
				variant="destructive"
				onclick={() => declineMutation.mutate(declineReason)}
				disabled={declineMutation.isPending}
				data-testid="application-decline-confirm"
			>
				{declineMutation.isPending ? 'Declining…' : 'Decline'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<!-- Adverse-action notice -->
<Dialog.Root open={showAdverseAction} onOpenChange={(v) => { if (!v) showAdverseAction = false; }}>
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>Generate adverse-action notice</Dialog.Title>
			<Dialog.Description>
				When you decline based on a screening report, the law (FCRA) requires sending the applicant
				this notice. It generates a PDF you can keep on file.
			</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-4">
			<div>
				<label for="adverse-reason" class="mb-1 block text-sm font-medium text-foreground">Reason</label>
				<textarea
					id="adverse-reason"
					bind:value={adverseReason}
					rows="3"
					placeholder="Reason for the decision…"
					class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground focus:border-ring focus:outline-none focus:ring-2 focus:ring-ring/40"
					data-testid="application-adverse-action-reason-input"
				></textarea>
			</div>
			<label class="flex items-center gap-2 text-sm text-foreground">
				<Checkbox bind:checked={adverseSendToApplicant} data-testid="application-adverse-action-send" />
				Email this notice to the applicant{application?.email ? ` (${application.email})` : ''}
			</label>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={() => (showAdverseAction = false)} data-testid="application-adverse-action-cancel">Cancel</Button>
			<Button
				onclick={() => adverseActionMutation.mutate()}
				disabled={adverseActionMutation.isPending}
				data-testid="application-adverse-action-confirm"
			>
				{adverseActionMutation.isPending ? 'Generating…' : 'Generate notice'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
