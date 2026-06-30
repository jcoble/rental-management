<script lang="ts">
	import { goto } from '$app/navigation';
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import {
		applications,
		type ApplicationResponse,
		type ScreeningResultResponse,
		type AdverseActionNoticeResponse,
		type UpdateApplicationRequest,
	} from '$lib/api/endpoints/applications';
	import { downloadDocument } from '$lib/api/endpoints/documents';
	import { ApiError } from '$lib/api/client';
	import { showSuccess, showWarning, showError, apiErrorMessage } from '$lib/utils/toast';
	import {
		formatApplicationAddress,
		canRunApplicationScreening,
		formatRequestedProperty,
		formatRequestedUnit,
	} from '$lib/applications/application-display';
	import { leaseCreateHrefForApprovedTenant } from '$lib/leases/lease-create-prefill';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import { Input } from '$lib/components/ui/input';
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
		Edit3,
	} from '@lucide/svelte';
	import { isMismatchedUnitSelection } from '$lib/unit/unit-membership-guard';

	// `applicationId` selects the record; `onDeleted` is the exit/close callback (the page has no
	// delete) — the host uses it to clear the selection / navigate back to the list.
	let {
		applicationId,
		onDeleted,
		expectedUnitId,
		onUnitMismatch,
	}: {
		applicationId: number;
		onDeleted: () => void;
		expectedUnitId?: number;
		onUnitMismatch?: () => void;
	} = $props();

	const queryClient = useQueryClient();
	const id = $derived(applicationId);

	const applicationQuery = createQuery(() => ({
		queryKey: ['application', id],
		queryFn: () => applications.get(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const application = $derived<ApplicationResponse | undefined>(applicationQuery.data);

	$effect(() => {
		if (isMismatchedUnitSelection(application, expectedUnitId)) onUnitMismatch?.();
	});

	const fullName = $derived(application ? `${application.firstName} ${application.lastName}` : '');
	const isOpen = $derived(
		application?.status === 'Submitted' || application?.status === 'UnderReview'
	);

	const STATUS_MAP = {
		Submitted: { label: 'Submitted', class: 'm3-tone-chip border m3-tone--info' },
		UnderReview: { label: 'Under Review', class: 'm3-tone-chip border m3-tone--primary' },
		Declined: { label: 'Declined', class: 'm3-tone-chip border m3-tone--error' },
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

	// ── Landlord corrections ─────────────────────────────────────────────────────
	type ApplicationEditForm = {
		firstName: string;
		lastName: string;
		email: string;
		phone: string;
		dateOfBirth: string;
		currentAddress: string;
		employer: string;
		monthlyIncome: string;
		desiredMoveInDate: string;
		notes: string;
	};

	function emptyApplicationEditForm(): ApplicationEditForm {
		return {
			firstName: '',
			lastName: '',
			email: '',
			phone: '',
			dateOfBirth: '',
			currentAddress: '',
			employer: '',
			monthlyIncome: '',
			desiredMoveInDate: '',
			notes: '',
		};
	}

	let showEdit = $state(false);
	let editForm = $state<ApplicationEditForm>(emptyApplicationEditForm());
	let editErrors = $state<Record<string, string>>({});

	function dateInputValue(value: string | null | undefined): string {
		if (!value) return '';
		const parsed = new Date(value);
		return isNaN(parsed.getTime()) ? '' : parsed.toISOString().slice(0, 10);
	}

	function openEditApplication() {
		if (!application) return;
		const formattedAddress = formatApplicationAddress(application);
		editForm = {
			firstName: application.firstName ?? '',
			lastName: application.lastName ?? '',
			email: application.email ?? '',
			phone: application.phone ?? '',
			dateOfBirth: dateInputValue(application.dateOfBirth),
			currentAddress: formattedAddress === '—' ? '' : formattedAddress,
			employer: application.employer ?? '',
			monthlyIncome: application.monthlyIncome == null ? '' : String(application.monthlyIncome),
			desiredMoveInDate: dateInputValue(application.desiredMoveInDate),
			notes: application.notes ?? '',
		};
		editErrors = {};
		showEdit = true;
	}

	function buildApplicationUpdate(): UpdateApplicationRequest | null {
		const errors: Record<string, string> = {};
		const firstName = editForm.firstName.trim();
		const lastName = editForm.lastName.trim();
		if (!firstName) errors.firstName = 'First name is required.';
		if (!lastName) errors.lastName = 'Last name is required.';

		const incomeText = editForm.monthlyIncome.trim();
		let income: number | null = null;
		if (incomeText) {
			const parsedIncome = Number(incomeText);
			if (!Number.isFinite(parsedIncome) || parsedIncome < 0) {
				errors.monthlyIncome = 'Enter a valid monthly income.';
			} else {
				income = parsedIncome;
			}
		}

		if (Object.keys(errors).length > 0) {
			editErrors = errors;
			return null;
		}

		editErrors = {};
		return {
			firstName,
			lastName,
			email: editForm.email.trim(),
			phone: editForm.phone.trim(),
			...(editForm.dateOfBirth
				? { dateOfBirth: editForm.dateOfBirth }
				: { clearDateOfBirth: true }),
			currentAddress: editForm.currentAddress.trim(),
			employer: editForm.employer.trim(),
			...(income == null ? { clearMonthlyIncome: true } : { monthlyIncome: income }),
			...(editForm.desiredMoveInDate
				? { desiredMoveInDate: editForm.desiredMoveInDate }
				: { clearDesiredMoveInDate: true }),
			notes: editForm.notes.trim(),
		};
	}

	const updateMutation = createMutation(() => ({
		mutationFn: (payload: UpdateApplicationRequest) => applications.update(id, payload),
		onSuccess: () => {
			showEdit = false;
			showSuccess('Application updated.');
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err, 'Application could not be updated.')),
	}));

	function submitEdit() {
		const payload = buildApplicationUpdate();
		if (!payload) return;
		updateMutation.mutate(payload);
	}

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
	const canScreen = $derived(
		canRunApplicationScreening(application?.status, application?.consentGiven)
	);

	const RECOMMENDATION_MAP = {
		Accept: { label: 'Accept', class: 'm3-tone-chip border m3-tone--success' },
		Conditional: { label: 'Conditional', class: 'm3-tone-chip border m3-tone--warning' },
		Decline: { label: 'Decline', class: 'm3-tone-chip border m3-tone--error' },
	};
	const SCREEN_STATUS_MAP = {
		Requested: { label: 'Requested', class: 'm3-tone-chip border m3-tone--info' },
		Completed: { label: 'Completed', class: 'm3-tone-chip border m3-tone--success' },
		Failed: { label: 'Failed', class: 'm3-tone-chip border m3-tone--error' },
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
			<Button variant="outline" class="mt-4" onclick={() => onDeleted()}>Back to Applications</Button>
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
					<Button variant="outline" class="gap-2" onclick={openEditApplication} data-testid="application-edit">
						<Edit3 class="h-4 w-4" /> Edit
					</Button>
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
				class="mb-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border bg-[var(--m3c-success-container)] text-[var(--m3c-on-success-container)] border-[color-mix(in_srgb,var(--success)_45%,transparent)] p-4"
				data-testid="application-tenant-banner"
			>
				<div class="flex items-center gap-2 text-sm">
					<CheckCircle2 class="h-5 w-5" />
					<span>This applicant was approved and a tenant record was created.</span>
				</div>
				<div class="flex flex-wrap items-center gap-2">
					<Button class="gap-2" href={leaseCreateHrefForApprovedTenant(tenantLinkId)} data-testid="application-create-lease">
						<Home class="h-4 w-4" /> Create lease <ArrowRight class="h-4 w-4" />
					</Button>
					<Button variant="outline" class="gap-2" onclick={() => goto(`/tenants/${tenantLinkId}`)} data-testid="application-view-tenant">
						<User class="h-4 w-4" /> View tenant <ArrowRight class="h-4 w-4" />
					</Button>
				</div>
			</div>
		{/if}

		<!-- Declined reason -->
		{#if application.status === 'Declined' && application.decisionReason}
			<div class="m3-error-surface mb-6 rounded-lg p-4 text-sm" data-testid="application-decline-reason">
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
					<div class="col-span-2">{@render fieldRow('Current address', formatApplicationAddress(application))}</div>
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
					{@render fieldRow('Property', formatRequestedProperty(application))}
					{@render fieldRow('Unit', formatRequestedUnit(application))}
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
							<CheckCircle2 class="h-4 w-4 text-[var(--success)]" />
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

			{#if application.hasScan}
				<Card.Root class="lg:col-span-2" data-testid="application-scan-card">
					<Card.Header>
						<Card.Title class="flex items-center gap-2 text-base"><FileText class="h-4 w-4" /> Scanned application</Card.Title>
					</Card.Header>
					<Card.Content>
						{#if application.scanIsImage}
							<a
								href="/application-file/{application.id}"
								target="_blank"
								rel="noopener noreferrer"
								data-testid="application-scan-link"
								aria-label="View scanned application full size"
								class="group inline-block"
							>
								<img
									src="/application-file/{application.id}?thumb=true"
									alt="Scanned application preview"
									class="max-h-80 w-auto rounded-md border border-border object-contain transition group-hover:ring-2 group-hover:ring-primary"
									loading="lazy"
								/>
								<span class="mt-1 block text-xs text-primary underline underline-offset-2 group-hover:text-primary/80">Open full size</span>
							</a>
						{:else}
							<a
								href="/application-file/{application.id}"
								target="_blank"
								rel="noopener noreferrer"
								data-testid="application-scan-link"
								class="inline-flex items-center gap-2 rounded-md border border-border bg-background px-4 py-3 text-sm font-medium text-foreground transition hover:border-primary hover:text-primary"
							>
								<FileText class="h-5 w-5 shrink-0" />
								<span>View scanned application document</span>
							</a>
						{/if}
					</Card.Content>
				</Card.Root>
			{/if}

			<!-- Screening -->
			<Card.Root class="lg:col-span-2" data-testid="application-screening-card">
				<Card.Header>
					<div class="flex flex-wrap items-center justify-between gap-3">
						<Card.Title class="flex items-center gap-2 text-base"><ScanSearch class="h-4 w-4" /> Screening</Card.Title>
							<div class="flex flex-col items-end gap-1">
								{#if isOpen}
									<Button
										class="gap-2"
										disabled={!canScreen || screenMutation.isPending}
										onclick={() => screenMutation.mutate()}
										data-testid="application-run-screening"
									>
										<ScanSearch class="h-4 w-4" />
										{screenMutation.isPending ? 'Screening…' : hasScreening ? 'Re-run screening' : 'Run screening'}
									</Button>
								{/if}
								{#if !isOpen}
									<p class="text-xs text-muted-foreground" data-testid="application-screening-terminal-note">
										Screening can only be run before a decision
									</p>
								{:else if !canScreen}
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
										class="mt-0.5 flex items-center gap-1.5 text-sm {latestScreening.hasCriminalRecord ? 'text-[var(--m3c-error)]' : 'text-foreground'}"
										data-testid="application-screening-criminal"
									>
										{#if latestScreening.hasCriminalRecord}
											<AlertCircle class="h-4 w-4 shrink-0" /> Records found — review
										{:else}
											<CheckCircle2 class="h-4 w-4 shrink-0 text-[var(--success)]" /> No criminal records found
										{/if}
									</p>
								</div>
								<div class="min-w-0">
									<p class="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Eviction records</p>
									<p
										class="mt-0.5 flex items-center gap-1.5 text-sm {latestScreening.hasEvictionRecord ? 'text-[var(--m3c-error)]' : 'text-foreground'}"
										data-testid="application-screening-eviction"
									>
										{#if latestScreening.hasEvictionRecord}
											<AlertCircle class="h-4 w-4 shrink-0" /> Records found — review
										{:else}
											<CheckCircle2 class="h-4 w-4 shrink-0 text-[var(--success)]" /> No eviction records found
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

<!-- Edit submitted application -->
<Dialog.Root open={showEdit} onOpenChange={(v) => { if (!v) showEdit = false; }}>
	<Dialog.Content class="max-w-3xl">
		<Dialog.Header>
			<Dialog.Title>Edit application</Dialog.Title>
			<Dialog.Description>
				Correct applicant details before making a decision. Legal consent and decision history stay unchanged.
			</Dialog.Description>
		</Dialog.Header>
		<div class="grid gap-4 sm:grid-cols-2">
			<div>
				<label for="application-edit-first" class="mb-1 block text-sm font-medium text-foreground">First name</label>
				<Input id="application-edit-first" bind:value={editForm.firstName} data-testid="application-edit-first-name" />
				{#if editErrors.firstName}<p class="mt-1 text-xs text-destructive">{editErrors.firstName}</p>{/if}
			</div>
			<div>
				<label for="application-edit-last" class="mb-1 block text-sm font-medium text-foreground">Last name</label>
				<Input id="application-edit-last" bind:value={editForm.lastName} data-testid="application-edit-last-name" />
				{#if editErrors.lastName}<p class="mt-1 text-xs text-destructive">{editErrors.lastName}</p>{/if}
			</div>
			<div>
				<label for="application-edit-email" class="mb-1 block text-sm font-medium text-foreground">Email</label>
				<Input id="application-edit-email" type="email" bind:value={editForm.email} data-testid="application-edit-email" />
			</div>
			<div>
				<label for="application-edit-phone" class="mb-1 block text-sm font-medium text-foreground">Phone</label>
				<Input id="application-edit-phone" bind:value={editForm.phone} data-testid="application-edit-phone" />
			</div>
			<div>
				<label for="application-edit-dob" class="mb-1 block text-sm font-medium text-foreground">Date of birth</label>
				<Input id="application-edit-dob" type="date" bind:value={editForm.dateOfBirth} data-testid="application-edit-date-of-birth" />
			</div>
			<div>
				<label for="application-edit-movein" class="mb-1 block text-sm font-medium text-foreground">Desired move-in</label>
				<Input id="application-edit-movein" type="date" bind:value={editForm.desiredMoveInDate} data-testid="application-edit-desired-move-in" />
			</div>
			<div>
				<label for="application-edit-employer" class="mb-1 block text-sm font-medium text-foreground">Employer</label>
				<Input id="application-edit-employer" bind:value={editForm.employer} data-testid="application-edit-employer" />
			</div>
			<div>
				<label for="application-edit-income" class="mb-1 block text-sm font-medium text-foreground">Monthly income</label>
				<Input id="application-edit-income" type="text" inputmode="decimal" bind:value={editForm.monthlyIncome} data-testid="application-edit-monthly-income" />
				{#if editErrors.monthlyIncome}<p class="mt-1 text-xs text-destructive">{editErrors.monthlyIncome}</p>{/if}
			</div>
			<div class="sm:col-span-2">
				<label for="application-edit-address" class="mb-1 block text-sm font-medium text-foreground">Current address</label>
				<Input id="application-edit-address" bind:value={editForm.currentAddress} data-testid="application-edit-current-address" />
			</div>
			<div class="sm:col-span-2">
				<label for="application-edit-notes" class="mb-1 block text-sm font-medium text-foreground">Notes</label>
				<textarea
					id="application-edit-notes"
					bind:value={editForm.notes}
					rows="3"
					class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground focus:border-ring focus:outline-none focus:ring-2 focus:ring-ring/40"
					data-testid="application-edit-notes"
				></textarea>
			</div>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={() => (showEdit = false)} data-testid="application-edit-cancel">Cancel</Button>
			<Button onclick={submitEdit} disabled={updateMutation.isPending} data-testid="application-edit-save">
				{updateMutation.isPending ? 'Saving…' : 'Save changes'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

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
