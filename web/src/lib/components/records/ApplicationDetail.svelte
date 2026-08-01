<script lang="ts">
	import { goto } from '$app/navigation';
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import {
		applications,
		type ApplicationResponse,
		type ApplicantScreeningResponse,
		type ScreeningRecommendation,
		type AdverseActionNoticeResponse,
		type UpdateApplicationRequest,
		type RecordApplicationFeeRequest,
	} from '$lib/api/endpoints/applications';
	import { downloadDocument } from '$lib/api/endpoints/documents';
	import { ApiError } from '$lib/api/client';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { parseForm, applicationFeeSchema } from '$lib/schemas';
	import { showSuccess, showWarning, showError, apiErrorMessage } from '$lib/utils/toast';
	import {
		formatApplicationAddress,
		canRunApplicationScreening,
		formatRequestedProperty,
		formatRequestedUnit,
	} from '$lib/applications/application-display';
	import { prepareMoveInHrefForApprovedTenant } from '$lib/leases/prepare-move-in-prefill';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
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
		DollarSign,
	} from '@lucide/svelte';
	import { isMismatchedUnitSelection } from '$lib/unit/unit-membership-guard';

	// `applicationId` selects the record; `onDeleted` is the exit/close callback (the page has no
	// delete) — the host uses it to clear the selection / navigate back to the list.
	let {
		applicationId,
		onDeleted,
		expectedUnitId,
		onUnitMismatch,
		prepareMoveInBasePath = '/applications',
		loadApplication = applications.get,
		showApplicationActions = true,
		showScreening = true,
		showTenantLink = true,
		applicationQueryScope = 'management',
	}: {
		applicationId: number;
		onDeleted: () => void;
		expectedUnitId?: number;
		onUnitMismatch?: () => void;
		prepareMoveInBasePath?: string;
		loadApplication?: (id: number) => Promise<ApplicationResponse>;
		showApplicationActions?: boolean;
		showScreening?: boolean;
		showTenantLink?: boolean;
		applicationQueryScope?: string;
	} = $props();

	const queryClient = useQueryClient();
	const id = $derived(applicationId);
	const portfolioId = $derived(getCurrentPortfolioId());

	const applicationQuery = createQuery(() => ({
		queryKey: ['application', applicationQueryScope, id],
		queryFn: () => loadApplication(id),
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

	// ── Record application fee in the application's pre-tenancy financial account ─────────────────
	let showRecordFee = $state(false);
	let feeAmount = $state('');
	let feeMethod = $state('');
	let feeEffectiveOn = $state('');
	let feeOperationKey = $state<string | null>(null);
	let feeErrors = $state<Record<string, string>>({});

	function openRecordFee() {
		feeAmount = '';
		feeMethod = '';
		feeEffectiveOn = '';
		feeOperationKey = null;
		feeErrors = {};
		showRecordFee = true;
	}

	const recordFeeMutation = createMutation(() => ({
		mutationFn: (body: RecordApplicationFeeRequest) => {
			feeOperationKey ??= crypto.randomUUID();
			return applications.recordFee(id, feeOperationKey, body);
		},
		onSuccess: () => {
			feeOperationKey = null;
			showRecordFee = false;
			showSuccess('Application fee recorded.');
			queryClient.invalidateQueries({ queryKey: ['application', id] });
			queryClient.invalidateQueries({ queryKey: ['accounting-summary', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['accounting-transactions'] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitRecordFee() {
		const result = parseForm(applicationFeeSchema, {
			amount: feeAmount,
			method: feeMethod,
			effectiveOn: feeEffectiveOn,
		});
		if (result.errors) {
			feeErrors = result.errors;
			return;
		}
		feeErrors = {};
		recordFeeMutation.mutate({
			amount: result.data.amount,
			method: result.data.method ?? null,
			currency: 'USD',
			effectiveOn: result.data.effectiveOn ?? null,
		});
	}

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
		enabled: showScreening && !isNaN(id) && id > 0,
	}));

	const latestScreening = $derived<ApplicantScreeningResponse | undefined>(
		screeningQuery.data?.screenings[0]
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
		Created: { label: 'Created', class: 'm3-tone-chip border m3-tone--info' },
		AwaitingProvider: { label: 'Connecting', class: 'm3-tone-chip border m3-tone--info' },
		AwaitingApplicant: { label: 'Waiting for applicant', class: 'm3-tone-chip border m3-tone--warning' },
		InProgress: { label: 'In progress', class: 'm3-tone-chip border m3-tone--primary' },
		Completed: { label: 'Completed', class: 'm3-tone-chip border m3-tone--success' },
		Failed: { label: 'Failed', class: 'm3-tone-chip border m3-tone--error' },
		Cancelled: { label: 'Cancelled', class: 'm3-tone-chip border' },
	};
	let screeningMode = $state<'Integrated' | 'External'>('Integrated');
	let externalProvider = $state('Zillow');
	let externalReference = $state('');
	let externalUrl = $state('');
	let externalCraName = $state('');
	let externalCraAddress = $state('');
	let externalCraPhone = $state('');
	let integratedScreeningOperationKey = $state<string | null>(null);
	let externalScreeningOperationKey = $state<string | null>(null);
	let completeExternalOperationKey = $state<string | null>(null);
	let updateCraOperationKey = $state<string | null>(null);
	let screeningDecisionOperationKey = $state<string | null>(null);
	let showCraContact = $state(false);
	let showScreeningDecision = $state(false);
	let craName = $state('');
	let craAddress = $state('');
	let craPhone = $state('');
	let screeningDecision = $state<ScreeningRecommendation>('Accept');
	let screeningDecisionReason = $state('');
	let consumerReportUsed = $state(false);

	const screenMutation = createMutation(() => ({
		mutationFn: () =>
			applications.startIntegratedScreening(
				id,
				(integratedScreeningOperationKey ??= crypto.randomUUID())
			),
		onSuccess: () => {
			showSuccess('Screening invitation created.');
			integratedScreeningOperationKey = null;
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

	const externalScreeningMutation = createMutation(() => ({
		mutationFn: () =>
			applications.trackExternalScreening(id, {
				operationKey: (externalScreeningOperationKey ??= crypto.randomUUID()),
				providerDisplayName: externalProvider.trim(),
				providerReference: externalReference.trim() || null,
				providerHostedUrl: externalUrl.trim() || null,
				creditReportingAgencyName: externalCraName.trim() || null,
				creditReportingAgencyAddress: externalCraAddress.trim() || null,
				creditReportingAgencyPhone: externalCraPhone.trim() || null,
				status: 'InProgress',
			}),
		onSuccess: () => {
			showSuccess('External screening added.');
			externalScreeningOperationKey = null;
			externalReference = '';
			externalUrl = '';
			externalCraName = '';
			externalCraAddress = '';
			externalCraPhone = '';
			queryClient.invalidateQueries({ queryKey: ['application-screening', id] });
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err, 'External screening could not be added.')),
	}));

	const completeExternalScreeningMutation = createMutation(() => ({
		mutationFn: (screeningId: number) =>
			applications.updateExternalScreening(id, screeningId, {
				operationKey: (completeExternalOperationKey ??= crypto.randomUUID()),
				status: 'Completed',
			}),
		onSuccess: () => {
			showSuccess('Outside screening marked complete.');
			completeExternalOperationKey = null;
			queryClient.invalidateQueries({ queryKey: ['application-screening', id] });
		},
		onError: (err) => showError(apiErrorMessage(err, 'Screening status could not be updated.')),
	}));

	function openCraContact(screening: ApplicantScreeningResponse) {
		craName = screening.creditReportingAgencyName ?? '';
		craAddress = screening.creditReportingAgencyAddress ?? '';
		craPhone = screening.creditReportingAgencyPhone ?? '';
		updateCraOperationKey = crypto.randomUUID();
		showCraContact = true;
	}

	const updateCraMutation = createMutation(() => ({
		mutationFn: (screeningId: number) =>
			applications.updateExternalScreening(id, screeningId, {
				operationKey: (updateCraOperationKey ??= crypto.randomUUID()),
				creditReportingAgencyName: craName.trim(),
				creditReportingAgencyAddress: craAddress.trim(),
				creditReportingAgencyPhone: craPhone.trim(),
			}),
		onSuccess: () => {
			updateCraOperationKey = null;
			showCraContact = false;
			showSuccess('Consumer reporting agency contact saved.');
			queryClient.invalidateQueries({ queryKey: ['application-screening', id] });
		},
		onError: (err) => showError(apiErrorMessage(err, 'Agency contact could not be saved.')),
	}));

	function openScreeningDecision(screening: ApplicantScreeningResponse) {
		screeningDecision = screening.decision ?? 'Accept';
		screeningDecisionReason = screening.decisionReason ?? '';
		consumerReportUsed = screening.consumerReportUsedForDecision;
		screeningDecisionOperationKey = crypto.randomUUID();
		showScreeningDecision = true;
	}

	const screeningDecisionMutation = createMutation(() => ({
		mutationFn: (screeningId: number) =>
			applications.recordScreeningDecision(id, screeningId, {
				operationKey: (screeningDecisionOperationKey ??= crypto.randomUUID()),
				decision: screeningDecision,
				reason: screeningDecisionReason.trim() || null,
				consumerReportUsed,
			}),
		onSuccess: () => {
			screeningDecisionOperationKey = null;
			showScreeningDecision = false;
			showSuccess('Screening decision recorded.');
			queryClient.invalidateQueries({ queryKey: ['application-screening', id] });
		},
		onError: (err) => showError(apiErrorMessage(err, 'Screening decision could not be recorded.')),
	}));

	function openApplicationOutcome(outcome: 'Approve' | 'Decline') {
		if (latestScreening?.status === 'Completed') {
			const compatible = outcome === 'Approve'
				? latestScreening.decision === 'Accept' || latestScreening.decision === 'Conditional'
				: latestScreening.decision === 'Decline';
			if (!compatible) {
				showWarning(`Record a ${outcome.toLowerCase()} screening decision first.`);
				openScreeningDecision(latestScreening);
				return;
			}
		}
		if (outcome === 'Approve') showApprove = true;
		else showDecline = true;
	}

	// ── Adverse-action notice ──────────────────────────────────────────────────────
	let showAdverseAction = $state(false);
	let adverseReason = $state('');
	let adverseSendToApplicant = $state(false);
	let adverseNotice = $state<AdverseActionNoticeResponse | null>(null);
	let adverseOperationKey = $state<string | null>(null);

	function openAdverseAction() {
		adverseReason = application?.decisionReason ?? '';
		adverseSendToApplicant = false;
		adverseOperationKey = crypto.randomUUID();
		showAdverseAction = true;
	}

	function adverseActionOperationKey(): string {
		return (adverseOperationKey ??= crypto.randomUUID());
	}

	const adverseActionMutation = createMutation(() => ({
		mutationFn: () =>
			applications.adverseAction(id, {
				operationKey: adverseActionOperationKey(),
				reason: adverseReason.trim() || undefined,
				sendToApplicant: adverseSendToApplicant,
			}),
		onSuccess: (notice) => {
			adverseNotice = notice;
			adverseOperationKey = null;
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
			{#if showApplicationActions && isOpen}
				<div class="flex flex-wrap items-center gap-2">
					<Button variant="outline" class="gap-2" onclick={openEditApplication} data-testid="application-edit">
						<Edit3 class="h-4 w-4" /> Edit
					</Button>
					<Button variant="outline" class="gap-2" onclick={openRecordFee} data-testid="application-record-fee">
						<DollarSign class="h-4 w-4" /> Record fee
					</Button>
					<Button class="gap-2" onclick={() => openApplicationOutcome('Approve')} data-testid="application-approve">
						<CheckCircle2 class="h-4 w-4" /> Approve
					</Button>
					<Button variant="outline" class="gap-2" onclick={() => openApplicationOutcome('Decline')} data-testid="application-decline">
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
					<Button class="gap-2" href={prepareMoveInHrefForApprovedTenant(tenantLinkId, id, application?.unitId ?? '', prepareMoveInBasePath)} data-testid="application-prepare-move-in">
						<Home class="h-4 w-4" /> Prepare move-in <ArrowRight class="h-4 w-4" />
					</Button>
					{#if showTenantLink}
						<Button variant="outline" class="gap-2" onclick={() => goto(`/tenants/${tenantLinkId}`)} data-testid="application-view-tenant">
							<User class="h-4 w-4" /> View tenant <ArrowRight class="h-4 w-4" />
						</Button>
					{/if}
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
			{#if showScreening}
			<Card.Root class="lg:col-span-2" data-testid="application-screening-card">
				<Card.Header>
					<Card.Title class="flex items-center gap-2 text-base"><ScanSearch class="h-4 w-4" /> Applicant screening</Card.Title>
					<p class="text-sm text-muted-foreground">
						Invite through Rental Command once a provider is connected, or track a screening completed in Zillow or another service.
					</p>
					<p class="text-xs text-muted-foreground">
						Do not paste Social Security numbers, identity answers, or report contents here. Review those only in the provider's secure site.
					</p>
				</Card.Header>
				<Card.Content class="space-y-5">
					<div class="grid gap-3 sm:grid-cols-2" data-testid="screening-mode-picker">
						<button
							type="button"
							class="rounded-xl border p-4 text-left transition {screeningMode === 'Integrated' ? 'border-primary bg-primary/5' : 'border-border'}"
							onclick={() => (screeningMode = 'Integrated')}
						>
							<p class="font-medium">Screen through Rental Command</p>
							<p class="mt-1 text-xs text-muted-foreground">The applicant securely enters sensitive information on the screening provider's site.</p>
						</button>
						<button
							type="button"
							class="rounded-xl border p-4 text-left transition {screeningMode === 'External' ? 'border-primary bg-primary/5' : 'border-border'}"
							onclick={() => (screeningMode = 'External')}
						>
							<p class="font-medium">Track an outside screening</p>
							<p class="mt-1 text-xs text-muted-foreground">Use Zillow or any other checker. Rental Command records progress but does not claim to sync it.</p>
						</button>
					</div>

					{#if screeningMode === 'Integrated'}
						<div class="rounded-xl border border-border p-4">
							<div class="flex flex-wrap items-center justify-between gap-3">
								<div>
									<p class="font-medium">{screeningQuery.data?.integratedProvider.displayName ?? 'Integrated screening'}</p>
									<p class="text-xs text-muted-foreground">
										{screeningQuery.data?.integratedProvider.isConfigured
											? 'Ready to create a secure applicant invitation.'
											: 'Provider selection is still being finalized. Outside screening remains available.'}
									</p>
								</div>
								<Button
									class="gap-2"
									disabled={!canScreen || !screeningQuery.data?.integratedProvider.isConfigured || screenMutation.isPending}
									onclick={() => screenMutation.mutate()}
									data-testid="application-run-screening"
								>
									<ScanSearch class="h-4 w-4" />
									{screenMutation.isPending ? 'Creating invitation…' : 'Invite applicant'}
								</Button>
							</div>
							{#if !application?.consentGiven}
								<p class="mt-3 text-xs text-muted-foreground">Applicant consent is required before an integrated screening can start.</p>
							{/if}
						</div>
					{:else}
						<div class="grid gap-3 rounded-xl border border-border p-4 sm:grid-cols-2" data-testid="external-screening-form">
							<label class="space-y-1 text-sm">
								<span class="font-medium">Screening service</span>
								<Input bind:value={externalProvider} placeholder="Zillow, another service, local agency…" />
							</label>
							<label class="space-y-1 text-sm">
								<span class="font-medium">Reference (optional)</span>
								<Input bind:value={externalReference} placeholder="Order or application number" />
							</label>
							<label class="space-y-1 text-sm sm:col-span-2">
								<span class="font-medium">Provider link (optional)</span>
								<Input bind:value={externalUrl} type="url" placeholder="https://…" />
							</label>
							<div class="space-y-3 rounded-lg bg-muted/40 p-3 sm:col-span-2">
								<p class="text-xs text-muted-foreground">Add the consumer reporting agency name, mailing address, and phone if its report may influence your decision. These are not required when no consumer report is used.</p>
								<div class="grid gap-3 sm:grid-cols-2">
									<Input bind:value={externalCraName} placeholder="Credit reporting agency name" />
									<Input bind:value={externalCraPhone} placeholder="Agency phone" />
									<Input class="sm:col-span-2" bind:value={externalCraAddress} placeholder="Agency mailing address" />
								</div>
							</div>
							<div class="sm:col-span-2">
								<Button
									disabled={!isOpen || !externalProvider.trim() || externalScreeningMutation.isPending}
									onclick={() => externalScreeningMutation.mutate()}
								>
									{externalScreeningMutation.isPending ? 'Adding…' : 'Add outside screening'}
								</Button>
							</div>
						</div>
					{/if}

					{#if screeningQuery.isLoading}
						<div class="flex items-center gap-2 text-sm text-muted-foreground">
							<div class="h-4 w-4 animate-spin rounded-full border-2 border-current border-t-transparent"></div>
							<span>Loading screening…</span>
						</div>
					{:else if latestScreening}
						<div class="space-y-4" data-testid="application-screening-result">
							<div class="flex flex-wrap items-center gap-2">
								<StatusBadge status={latestScreening.status} map={SCREEN_STATUS_MAP} />
								<span class="m3-tone-chip border">{latestScreening.mode}</span>
								{#if latestScreening.decision}
									<StatusBadge
										status={latestScreening.decision}
										map={RECOMMENDATION_MAP}
									/>
								{/if}
								<span class="text-xs text-muted-foreground">
									Updated {fmtDateTime(latestScreening.lastStatusAtUtc)}
								</span>
							</div>
							<div class="rounded-lg border border-border bg-muted/30 p-3 text-sm">
								<p class="font-medium text-foreground">{latestScreening.statusSummary}</p>
								<p class="mt-1 text-muted-foreground">Next: {latestScreening.nextAction}</p>
							</div>
							<div class="grid gap-x-4 gap-y-4 text-sm sm:grid-cols-3">
								{@render fieldRow('Provider', latestScreening.providerDisplayName)}
								{@render fieldRow('Reference', latestScreening.providerReference || '—')}
								{@render fieldRow('Consumer report used', latestScreening.decision ? (latestScreening.consumerReportUsedForDecision ? 'Yes' : 'No') : 'Not recorded yet')}
								{@render fieldRow('Agency contact', latestScreening.hasCompleteCreditReportingAgencyContact ? 'Complete' : (latestScreening.decision && !latestScreening.consumerReportUsedForDecision ? 'Not required for this decision' : 'Missing'))}
								{#if latestScreening.decisionReason}
									{@render fieldRow('Decision reason', latestScreening.decisionReason)}
								{/if}
								{#if latestScreening.providerHostedUrl}
									<a class="text-primary underline underline-offset-2" href={latestScreening.providerHostedUrl} target="_blank" rel="noopener noreferrer">Open provider</a>
								{/if}
							</div>
							{#if latestScreening.mode === 'External' && latestScreening.status !== 'Completed' && latestScreening.status !== 'Cancelled'}
								<Button
									variant="outline"
									disabled={completeExternalScreeningMutation.isPending}
									onclick={() => completeExternalScreeningMutation.mutate(latestScreening.id)}
								>
									Mark outside screening complete
								</Button>
							{/if}
							<div class="flex flex-wrap gap-2">
								{#if latestScreening.mode === 'External'}
									<Button variant="outline" onclick={() => openCraContact(latestScreening)}>
										{latestScreening.hasCompleteCreditReportingAgencyContact ? 'Update agency contact' : 'Add agency contact'}
									</Button>
								{/if}
								{#if latestScreening.status === 'Completed'}
									<Button variant="outline" onclick={() => openScreeningDecision(latestScreening)}>
										{latestScreening.decision ? 'Update screening decision' : 'Record screening decision'}
									</Button>
								{/if}
							</div>
							{#if latestScreening.status === 'Completed' && !latestScreening.decision}
								<p class="text-xs text-muted-foreground">Record whether the consumer report influenced your decision before approving or declining this application.</p>
							{/if}
							{#if latestScreening.decision && latestScreening.consumerReportUsedForDecision && !latestScreening.hasCompleteCreditReportingAgencyContact}
								<p class="text-xs text-destructive">Agency name, mailing address, and phone are required for a report-based decision. Add them to continue to adverse action.</p>
							{/if}
						</div>
					{:else}
						<p class="text-sm text-muted-foreground" data-testid="application-screening-empty">
							No screening has been added yet. Choose either path above.
						</p>
					{/if}
				</Card.Content>

				<!-- Adverse-action: shown once declined (or about to be) and a screening exists -->
				{#if hasScreening && application.status === 'Declined'}
					<Card.Footer class="flex-col items-stretch gap-3 border-t pt-4">
						<p class="text-xs text-muted-foreground" data-testid="application-adverse-action-help">
							{latestScreening?.consumerReportUsedForDecision
								? 'When you decline based on a report, the law requires sending the applicant this notice.'
								: 'No adverse-action notice is offered here unless you record that a consumer report influenced the decline.'}
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
						{:else if latestScreening?.canGenerateAdverseAction}
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
			{/if}
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
				<DatePicker id="application-edit-dob" bind:value={editForm.dateOfBirth} testid="application-edit-date-of-birth" />
			</div>
			<div>
				<label for="application-edit-movein" class="mb-1 block text-sm font-medium text-foreground">Desired move-in</label>
				<DatePicker id="application-edit-movein" bind:value={editForm.desiredMoveInDate} testid="application-edit-desired-move-in" />
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

<!-- Correct consumer reporting agency contact without creating another screening -->
<Dialog.Root open={showCraContact} onOpenChange={(v) => { if (!v) showCraContact = false; }}>
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>Consumer reporting agency contact</Dialog.Title>
			<Dialog.Description>
				Required only when a consumer report influences the decision. Correcting this contact updates the existing screening; it does not create a new one.
			</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-3">
			<label class="space-y-1 text-sm">
				<span class="font-medium">Agency name</span>
				<Input bind:value={craName} placeholder="Consumer reporting agency" />
			</label>
			<label class="space-y-1 text-sm">
				<span class="font-medium">Mailing address</span>
				<Input bind:value={craAddress} placeholder="Street, city, state, postal code" />
			</label>
			<label class="space-y-1 text-sm">
				<span class="font-medium">Phone</span>
				<Input bind:value={craPhone} placeholder="Agency phone" />
			</label>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={() => (showCraContact = false)}>Cancel</Button>
			<Button
				onclick={() => latestScreening && updateCraMutation.mutate(latestScreening.id)}
				disabled={!craName.trim() || !craAddress.trim() || !craPhone.trim() || updateCraMutation.isPending}
			>
				{updateCraMutation.isPending ? 'Saving…' : 'Save agency contact'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<!-- Explicit landlord decision about the completed screening -->
<Dialog.Root open={showScreeningDecision} onOpenChange={(v) => { if (!v) showScreeningDecision = false; }}>
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>Record screening decision</Dialog.Title>
			<Dialog.Description>
				Record your decision and whether a consumer report influenced it. Rental Command stores this workflow metadata, not the report or its findings.
			</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-4">
			<div class="space-y-1 text-sm">
				<span class="font-medium">Decision</span>
				<Select.Root type="single" bind:value={screeningDecision}>
					<Select.Trigger class="h-10 w-full" data-testid="application-screening-decision-select">
						{screeningDecision}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="Accept" label="Accept">Accept</Select.Item>
						<Select.Item value="Conditional" label="Conditional">Conditional</Select.Item>
						<Select.Item value="Decline" label="Decline">Decline</Select.Item>
					</Select.Content>
				</Select.Root>
			</div>
			<label class="space-y-1 text-sm">
				<span class="font-medium">Principal reason</span>
				<textarea bind:value={screeningDecisionReason} rows="3" class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm" placeholder="Required when a consumer report influenced the decision"></textarea>
			</label>
			<label class="flex items-start gap-3 rounded-lg border border-border p-3 text-sm">
				<Checkbox bind:checked={consumerReportUsed} />
				<span>
					<strong class="block">A consumer report influenced this decision</strong>
					<span class="text-xs text-muted-foreground">Turn this on even if the report was only one factor. Adverse-action guidance is enabled for a decline.</span>
				</span>
			</label>
			{#if consumerReportUsed && !latestScreening?.hasCompleteCreditReportingAgencyContact}
				<p class="text-xs text-destructive">Save the agency name, mailing address, and phone before recording a report-based decision.</p>
			{/if}
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={() => (showScreeningDecision = false)}>Cancel</Button>
			<Button
				onclick={() => latestScreening && screeningDecisionMutation.mutate(latestScreening.id)}
				disabled={screeningDecisionMutation.isPending || (consumerReportUsed && (!screeningDecisionReason.trim() || !latestScreening?.hasCompleteCreditReportingAgencyContact))}
			>
				{screeningDecisionMutation.isPending ? 'Saving…' : 'Save decision'}
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

<!-- Record application fee (lease-less income against the application's property) -->
<Dialog.Root open={showRecordFee} onOpenChange={(v) => { if (!v) showRecordFee = false; }}>
	<Dialog.Content class="max-w-md">
		<Dialog.Header>
			<Dialog.Title>Record application fee</Dialog.Title>
			<Dialog.Description>
				Record a paid application/screening fee as income for {fullName}. It posts to the
				application's property and shows on the accounting ledger and Schedule E — no lease required.
			</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-3" data-testid="application-fee-form">
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Amount</span>
				<Input
					data-testid="application-fee-amount-input"
					bind:value={feeAmount}
					type="text"
					inputmode="decimal"
					mask="currency"
					placeholder="0.00"
				/>
				{#if feeErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="application-fee-amount-error">{feeErrors.amount}</p>{/if}
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Method (optional)</span>
				<Input
					data-testid="application-fee-method-input"
					bind:value={feeMethod}
					placeholder="e.g. Card, Cash, Check"
				/>
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Effective date (optional)</span>
				<DatePicker
					id="application-fee-paid-date-input"
					testid="application-fee-paid-date-input"
					value={feeEffectiveOn}
					onchange={(v) => (feeEffectiveOn = v)}
					placeholder="Defaults to today"
				/>
			</div>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={() => (showRecordFee = false)} data-testid="application-fee-cancel">Cancel</Button>
			<Button onclick={submitRecordFee} disabled={recordFeeMutation.isPending} data-testid="application-fee-save">
				{recordFeeMutation.isPending ? 'Recording…' : 'Record fee'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
