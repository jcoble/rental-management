<script lang="ts">
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { page } from '$app/state';
	import {
		leaseManagements,
		type LeaseAgreementDraftMutationResponse
	} from '$lib/api/endpoints/lease-managements';
	import type { IssueLeaseAgreementResponse } from '$lib/api/endpoints/lease-managements';
	import {
		leaseAddendums,
		type IssueLeaseAddendumResponse,
		type LeaseAddendumDraftMutationResponse,
		type LeaseAddendumHistoryItem
	} from '$lib/api/endpoints/lease-addendums';
	import type { LeaseAgreementSummary } from '$lib/types';
	import type { WorkspaceExperience } from '$lib/types/user';
	import AddendumCorrectionDialog from '$lib/components/leases/AddendumCorrectionDialog.svelte';
	import AddendumCreateDialog from '$lib/components/leases/AddendumCreateDialog.svelte';
	import AddendumDraftDialog from '$lib/components/leases/AddendumDraftDialog.svelte';
	import AgreementDraftDialog from '$lib/components/leases/AgreementDraftDialog.svelte';
	import AgreementIssuedRecoveryDialog from '$lib/components/leases/AgreementIssuedRecoveryDialog.svelte';
	import AgreementSignatureProgress from '$lib/components/leases/AgreementSignatureProgress.svelte';
	import AgreementSuccessorDialog from '$lib/components/leases/AgreementSuccessorDialog.svelte';
	import EndingDispositionDialog from '$lib/components/leases/EndingDispositionDialog.svelte';
	import HouseholdManagementDialog from '$lib/components/leases/HouseholdManagementDialog.svelte';
	import PossessionActions from '$lib/components/leases/PossessionActions.svelte';
	import TenantNoticeDialog from '$lib/components/notices/TenantNoticeDialog.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Card, CardContent, CardHeader, CardTitle } from '$lib/components/ui/card';
	import { BellRing, CalendarClock, Eye, FileDown, FilePenLine, FilePlus2, Home, ScanLine, Users } from '@lucide/svelte';
	import { apiErrorMessage, showError } from '$lib/utils/toast';
	import { formatDateOnly } from '$lib/utils/date';
	import { money } from '$lib/components/unit/money';
	import type { LeaseManagementParty } from '$lib/types';
	import type { ReturnPossessionActiveTenantUserAccess } from '$lib/api/endpoints/lease-managements';

	type SuccessorType = 'Correction' | 'Restatement' | 'Renewal' | 'MonthToMonth';
	type SuccessorSelection = { source: LeaseAgreementSummary; changeType: SuccessorType };
	type AddendumBaseAgreement = Pick<LeaseAgreementSummary, 'leaseAgreementId' | 'agreementNumber'>;

	const AGREEMENT_PAGE_SIZE = 10;
	const ADDENDUM_PAGE_SIZE = 10;
	const queryClient = useQueryClient();
	let { leaseManagementId }: { leaseManagementId: number } = $props();
	let agreementSkip = $state(0);
	let editAgreementId = $state<number | null>(null);
	let editAgreementSource = $state<LeaseAgreementSummary | null>(null);
	let editAgreementCanCancel = $state(false);
	let signatureProgressAgreementId = $state<number | null>(null);
	let successorSelection = $state<SuccessorSelection | null>(null);
	let issuedRecoverySource = $state<LeaseAgreementSummary | null>(null);
	let addendumSkip = $state(0);
	let editAddendumId = $state<number | null>(null);
	let createAddendumBase = $state<AddendumBaseAgreement | null>(null);
	let correctionAddendum = $state<LeaseAddendumHistoryItem | null>(null);
	let endingDispositionOpen = $state(false);
	let noticeDialogOpen = $state(false);
	type HouseholdAction = { mode: 'add' | 'change' | 'end' | 'grant' | 'revoke'; party?: LeaseManagementParty; access?: ReturnPossessionActiveTenantUserAccess };
	let householdAction = $state<HouseholdAction | null>(null);
	const activeExperience = $derived(page.data.access?.selectedContext.activeExperience ?? null);
	const activeCapabilities = $derived(new Set(
		page.data.access?.navigation.find((entry: { experience: WorkspaceExperience; capabilityKeys: string[] }) =>
			entry.experience === activeExperience
		)?.capabilityKeys ?? []
	));
	const canManageHousehold = $derived(activeCapabilities.has('rentals.manage') || activeCapabilities.has('leasing.onboarding.manage'));
	const canPrepareAgreements = $derived(activeCapabilities.has('rentals.manage') || activeCapabilities.has('leasing.agreements.prepare'));
	const canManageTenantNotices = $derived(activeCapabilities.has('notifications.tenant-notices.manage'));

	function endingDispositionLabel(value: string) {
		switch (value) {
			case 'OfferRenewal': return 'Renew / continue';
			case 'OfferMonthToMonth': return 'Continue month-to-month';
			case 'NonRenewalMoveOut': return 'Move out / end';
			default: return 'Not decided';
		}
	}

	const relationshipQuery = createQuery(() => ({
		queryKey: ['lease-managements', leaseManagementId],
		queryFn: () => leaseManagements.get(leaseManagementId),
		enabled: Number.isInteger(leaseManagementId) && leaseManagementId > 0
	}));
	const currentNoticeParty = $derived.by(() => {
		const detail = relationshipQuery.data;
		if (!detail) return null;

		return detail.parties.find((party) => party.isCurrent && party.tenantId === detail.summary.primaryTenantId)
			?? detail.parties.find((party) => party.isCurrent && (party.role === 'PrimaryTenant' || party.role === 'CoTenant'))
			?? null;
	});
	const agreementsQuery = createQuery(() => ({
		queryKey: ['lease-managements', leaseManagementId, 'agreements', agreementSkip],
		queryFn: () =>
			leaseManagements.agreements(leaseManagementId, {
				skip: agreementSkip,
				take: AGREEMENT_PAGE_SIZE,
				sort: '-versionNumber'
			}),
		enabled: Number.isInteger(leaseManagementId) && leaseManagementId > 0
	}));
	const addendaQuery = createQuery(() => ({
		queryKey: ['lease-managements', leaseManagementId, 'addenda', addendumSkip],
		queryFn: () => leaseAddendums.listPage(leaseManagementId, {
			skip: addendumSkip,
			take: ADDENDUM_PAGE_SIZE,
			sort: '-effectiveFromOn'
		}),
		enabled: Number.isInteger(leaseManagementId) && leaseManagementId > 0
	}));
	const householdContextQuery = createQuery(() => ({
		queryKey: ['lease-managements', leaseManagementId, 'household-context'],
		queryFn: () => leaseManagements.getCurrentPartiesContext(leaseManagementId),
		enabled: Number.isInteger(leaseManagementId) && leaseManagementId > 0
	}));

	function saveBlob(blob: Blob, fileName: string) {
		const url = URL.createObjectURL(blob);
		const anchor = document.createElement('a');
		anchor.href = url;
		anchor.download = fileName;
		anchor.click();
		URL.revokeObjectURL(url);
	}

	async function downloadAgreement(agreementId: number, artifactId: number, fileName: string) {
		try {
			await saveBlob(
				await leaseManagements.downloadArtifact(
					leaseManagementId,
					agreementId,
					artifactId
				),
				fileName
			);
		} catch (error) {
			showError(apiErrorMessage(error, 'Agreement download failed.'));
		}
	}

	async function downloadSourceScan(agreement: LeaseAgreementSummary) {
		try {
			const blob = await leaseManagements.downloadSourceScan(
				leaseManagementId,
				agreement.leaseAgreementId
			);
			const extension = blob.type === 'application/pdf' ? '.pdf' : '';
			await saveBlob(blob, `${agreement.agreementNumber}-source${extension}`);
		} catch (error) {
			showError(apiErrorMessage(error, 'Source scan download failed.'));
		}
	}

	async function downloadAddendum(addendumId: number, artifactId: number, fileName: string) {
		try {
			await saveBlob(
				await leaseAddendums.downloadArtifact(leaseManagementId, addendumId, artifactId),
				fileName
			);
		} catch (error) {
			showError(apiErrorMessage(error, 'Addendum download failed.'));
		}
	}

	async function refreshLease() {
		await queryClient.invalidateQueries({ queryKey: ['lease-managements', leaseManagementId] });
	}

	function activeAccess(partyId: number) {
		return householdContextQuery.data?.activeTenantUserAccesses.find((access) => access.leaseManagementPartyId === partyId);
	}

	function canShowHouseholdAction(party: LeaseManagementParty) {
		return party.isCurrent || party.canGrantTenantPortalAccess;
	}

	function canGrantAccess(party: LeaseManagementParty) {
		return party.canGrantTenantPortalAccess && Boolean(party.email) && !activeAccess(party.leaseManagementPartyId);
	}

	function loginStatus(access: ReturnPossessionActiveTenantUserAccess | undefined) {
		if (!access) return 'not granted';
		if (access.hasPendingActivationInvitation) return `pending for ${access.userEmail}`;
		if (access.isPortalLoginReady || !access.requiresAccountActivation) return `active for ${access.userEmail}`;
		return `setup required for ${access.userEmail}`;
	}

	async function handleSuccessorCreated(result: LeaseAgreementDraftMutationResponse) {
		editAgreementSource = successorSelection?.source ?? null;
		editAgreementCanCancel = true;
		successorSelection = null;
		agreementSkip = 0;
		await refreshLease();
		editAgreementId = result.leaseAgreementId;
	}

	async function handleIssuedRecoveryCreated(result: LeaseAgreementDraftMutationResponse) {
		editAgreementSource = issuedRecoverySource;
		editAgreementCanCancel = true;
		issuedRecoverySource = null;
		agreementSkip = 0;
		await refreshLease();
		editAgreementId = result.leaseAgreementId;
	}

	function openAgreementDraft(agreement: LeaseAgreementSummary) {
		const sourceId = agreement.replacesAgreementId ?? agreement.renewsAgreementId;
		editAgreementSource = sourceId
			? (agreementsQuery.data?.items.find((candidate) => candidate.leaseAgreementId === sourceId) ?? null)
			: null;
		editAgreementCanCancel = Boolean(sourceId);
		editAgreementId = agreement.leaseAgreementId;
	}

	async function handleDraftCanceled() {
		editAgreementId = null;
		editAgreementSource = null;
		editAgreementCanCancel = false;
		agreementSkip = 0;
		await refreshLease();
	}

	async function handleIssued(result: IssueLeaseAgreementResponse) {
		editAgreementId = null;
		editAgreementSource = null;
		editAgreementCanCancel = false;
		agreementSkip = 0;
		await refreshLease();
		signatureProgressAgreementId = result.leaseAgreementId;
	}

	async function handleAddendumDraftCreated(result: LeaseAddendumDraftMutationResponse) {
		createAddendumBase = null;
		correctionAddendum = null;
		addendumSkip = 0;
		await refreshLease();
		editAddendumId = result.leaseAddendumId;
	}

	async function handleAddendumIssued(_result: IssueLeaseAddendumResponse) {
		editAddendumId = null;
		addendumSkip = 0;
		await refreshLease();
	}
</script>

<svelte:head>
	<title>{relationshipQuery.data?.summary.primaryTenantName
		? `${relationshipQuery.data.summary.primaryTenantName} - Lease - Rental Command`
		: 'Lease - Rental Command'}</title>
</svelte:head>

{#if relationshipQuery.isLoading}
	<LoadingState
		label="Loading tenant and lease relationship"
		variant="page"
		testid="lease-relationship-loading"
	/>
{:else if relationshipQuery.error || !relationshipQuery.data}
	<div class="rounded-xl border border-destructive/30 bg-destructive/5 p-6">
		<h1 class="text-xl font-semibold">Tenant and lease relationship unavailable</h1>
		<p class="mt-2 text-sm text-muted-foreground">
			It may no longer exist or you may not have access.
		</p>
		<div class="mt-4 flex flex-wrap gap-2">
			<Button variant="outline" onclick={() => relationshipQuery.refetch()}>Try again</Button>
			<Button href="/leases" variant="ghost">Back to leases</Button>
		</div>
	</div>
{:else}
	{@const detail = relationshipQuery.data}
	{@const summary = detail.summary}
	{@const governingAgreement = summary.leaseAgreementId && summary.agreementNumber ? { leaseAgreementId: summary.leaseAgreementId, agreementNumber: summary.agreementNumber } : null}
	<div class="space-y-6">
		<PageHeader
			title={summary.primaryTenantName ?? 'Tenant & lease'}
			description={`${summary.propertyName}${summary.unitNumber ? ` · ${summary.unitNumber}` : ''} · ${summary.relationshipNumber}`}
		>
			{#snippet actions()}
				<Button href={activeExperience === 'Leasing' ? `/leasing/rentals/${summary.unitId}` : `/units/${summary.unitId}`} variant="outline" class="gap-2"
					><Home class="h-4 w-4" /> Open rental</Button
				>
			{/snippet}
		</PageHeader>

		{#if summary.hasReconciliationException}
			<div class="rounded-xl border border-warning/40 bg-warning/10 p-4 text-sm">
				<p class="font-medium">Lease and possession need reconciliation</p>
				<p class="mt-1 text-muted-foreground">Review the governing agreement and possession dates before taking another lifecycle action.</p>
			</div>
		{/if}

			<div class="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
				<Card class="md:col-span-2 xl:col-span-3">
					<CardHeader><CardTitle class="text-base">Lease at a glance</CardTitle></CardHeader>
					<CardContent class="space-y-4">
						<dl class="grid gap-x-6 gap-y-4 sm:grid-cols-2 xl:grid-cols-3">
							<div class="space-y-1">
								<dt class="text-xs font-medium text-muted-foreground">Current status</dt>
								<dd><StatusBadge status={summary.lifecycle} /></dd>
							</div>
							<div class="space-y-1">
								<dt class="text-xs font-medium text-muted-foreground">Current agreement</dt>
								<dd class="font-medium">{summary.agreementNumber ?? 'Not issued'}</dd>
							</div>
							<div class="space-y-1">
								<dt class="text-xs font-medium text-muted-foreground">Agreement status</dt>
								<dd class="font-medium">{summary.agreementStatus ?? 'No governing agreement'}</dd>
							</div>
							<div class="space-y-1">
								<dt class="text-xs font-medium text-muted-foreground">Term</dt>
								<dd class="font-medium">
									{summary.termStartOn
										? `${formatDateOnly(summary.termStartOn)} – ${summary.termEndOn ? formatDateOnly(summary.termEndOn) : 'Month-to-month'}`
										: 'Not set'}
								</dd>
							</div>
							<div class="space-y-1">
								<dt class="text-xs font-medium text-muted-foreground">Base rent</dt>
								<dd class="font-medium">{summary.baseRentAmount == null ? 'Not set' : `${money(summary.baseRentAmount)} per month`}</dd>
							</div>
							<div class="space-y-1">
								<dt class="text-xs font-medium text-muted-foreground">Possession</dt>
								<dd class="font-medium">
									{summary.possessionGivenAtUtc
										? `Given ${new Date(summary.possessionGivenAtUtc).toLocaleDateString()}`
										: summary.plannedPossessionAtUtc
											? `Planned ${formatDateOnly(summary.plannedPossessionAtUtc)}`
											: 'Not yet scheduled'}
								</dd>
							</div>
							{#if summary.upcomingLeaseAgreementId}
								<div class="space-y-1">
									<dt class="text-xs font-medium text-muted-foreground">Upcoming agreement</dt>
									<dd class="font-medium">
										{summary.upcomingAgreementNumber ?? `Agreement #${summary.upcomingLeaseAgreementId}`}
										{summary.upcomingTermStartOn ? ` · starts ${formatDateOnly(summary.upcomingTermStartOn)}` : ''}
										{summary.upcomingAgreementStatus ? ` · ${summary.upcomingAgreementStatus}` : ''}
									</dd>
								</div>
							{/if}
							<div class="space-y-1">
								<dt class="text-xs font-medium text-muted-foreground">Ending plan</dt>
								<dd class="font-medium">{endingDispositionLabel(summary.endingDisposition)}</dd>
							</div>
							{#if summary.endingDispositionDecidedAtUtc}
								<div class="space-y-1">
									<dt class="text-xs font-medium text-muted-foreground">Decision recorded</dt>
									<dd class="font-medium">{new Date(summary.endingDispositionDecidedAtUtc).toLocaleDateString()}</dd>
								</div>
							{/if}
							{#if summary.plannedMoveOutAtUtc}
								<div class="space-y-1">
									<dt class="text-xs font-medium text-muted-foreground">Planned move-out</dt>
									<dd class="font-medium">{formatDateOnly(summary.plannedMoveOutAtUtc)}</dd>
								</div>
							{/if}
						</dl>
						{#if canManageTenantNotices || (canPrepareAgreements && summary.possessionGivenAtUtc && !summary.possessionReturnedAtUtc && !summary.canceledAtUtc)}
							<div class="flex flex-wrap gap-2" data-testid="lease-lifecycle-actions">
								{#if canManageTenantNotices}
									<Button
										variant="outline"
										size="sm"
										class="gap-2"
										disabled={!currentNoticeParty}
										title={currentNoticeParty ? `Create a notice for ${currentNoticeParty.tenantName}` : 'A current tenant is required'}
										onclick={() => (noticeDialogOpen = true)}
										data-testid="lease-create-send-notice"
									>
										<BellRing class="h-4 w-4" /> Create / Send notice
									</Button>
								{/if}
								{#if canPrepareAgreements && summary.possessionGivenAtUtc && !summary.possessionReturnedAtUtc && !summary.canceledAtUtc}
									<Button variant="outline" size="sm" class="gap-2" onclick={() => (endingDispositionOpen = true)}>
										<CalendarClock class="h-4 w-4" /> Record decision
									</Button>
								{/if}
							</div>
						{/if}
					</CardContent>
				</Card>
				<Card class="md:col-span-2 xl:col-span-1">
					<CardHeader><CardTitle class="text-base">Tenant account</CardTitle></CardHeader>
					<CardContent class="space-y-3">
						<p class="font-medium">{summary.tenantAccountId ? `Account #${summary.tenantAccountId}` : 'Not opened'}</p>
						<p class="text-sm text-muted-foreground">Continues across renewals and corrections</p>
						{#if summary.tenantAccountId}
							<Button href={`/units/${summary.unitId}?tab=money&view=tenant-account&tenantAccount=${summary.tenantAccountId}`} variant="outline" size="sm">View tenant account</Button>
						{/if}
					</CardContent>
				</Card>
			</div>

		<PossessionActions {summary} canManage={canManageHousehold} onchanged={refreshLease} />

		{#if endingDispositionOpen && canPrepareAgreements}
			<EndingDispositionDialog
				{summary}
				onclose={() => (endingDispositionOpen = false)}
				onrecorded={refreshLease}
			/>
		{/if}

		<Card>
			<CardHeader>
				<div class="flex flex-wrap items-start justify-between gap-3">
					<div><CardTitle class="flex items-center gap-2"><Users class="h-5 w-5" /> Household and responsibility</CardTitle><p class="mt-1 text-sm text-muted-foreground">Membership and login access can change over time. Signed agreement PDFs remain immutable.</p></div>
					{#if canManageHousehold}<Button size="sm" onclick={() => (householdAction = { mode: 'add' })}>Add person</Button>{/if}
				</div>
			</CardHeader>
			<CardContent class="space-y-5">
				{#if householdContextQuery.isLoading}
					<LoadingState label="Loading household" testid="lease-household-loading" />
				{:else if householdContextQuery.isError}
					<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="lease-household-error">
						<p class="text-sm font-medium text-destructive">Household details could not be loaded.</p>
						<Button class="mt-3" variant="outline" size="sm" onclick={() => householdContextQuery.refetch()}>Try again</Button>
					</div>
				{:else if detail.parties.length === 0}
					<p class="text-sm text-muted-foreground">No effective parties.</p>
				{:else}
					<div>
						<h3 class="text-sm font-semibold">Current and scheduled household</h3>
						<div class="divide-y">
						{#each detail.parties as party}
							{#if canShowHouseholdAction(party)}
							{@const access = activeAccess(party.leaseManagementPartyId)}
							<div class="flex flex-col gap-3 py-3 sm:flex-row sm:items-center sm:justify-between">
								<div>
									<p class="font-medium">{party.tenantName}</p>
									<p class="text-sm text-muted-foreground">{party.email ?? party.phone ?? 'No contact information'}</p>
									<p class="text-xs text-muted-foreground">{party.isCurrent ? `Effective since ${party.effectiveFrom}` : `Scheduled for ${party.effectiveFrom}`} · Login {loginStatus(access)}</p>
								</div>
								<div class="flex flex-wrap items-center gap-2"><StatusBadge status={party.role} />{#if canManageHousehold}<Button size="sm" variant="outline" onclick={() => (householdAction = { mode: 'change', party })}>Change role</Button><Button size="sm" variant="outline" onclick={() => (householdAction = { mode: 'end', party })}>End membership</Button>{#if access}<Button size="sm" variant="outline" onclick={() => (householdAction = { mode: 'revoke', party, access })}>Revoke resident login</Button>{:else}<Button size="sm" variant="outline" disabled={!canGrantAccess(party)} title={party.email ? 'Create relationship-scoped resident login' : 'Add an email to this person first'} onclick={() => (householdAction = { mode: 'grant', party })}>Create resident login</Button>{/if}{/if}</div>
							</div>
							{/if}
						{/each}
						</div>
					</div>
					<div>
						<h3 class="text-sm font-semibold">Household history</h3>
						<div class="divide-y">
						{#each detail.parties as party}
							{#if !canShowHouseholdAction(party)}<div class="flex items-center justify-between gap-4 py-3"><div><p class="font-medium">{party.tenantName}</p><p class="text-xs text-muted-foreground">{party.effectiveFrom} through {party.effectiveThrough ?? 'current'}</p></div><StatusBadge status={party.role} /></div>{/if}
						{/each}
					</div>
					</div>
				{/if}
			</CardContent>
		</Card>

		<Card>
			<CardHeader>
				<CardTitle>Agreement versions</CardTitle>
				<p class="text-sm text-muted-foreground">Draft, signature, governing, source, and immutable artifact history.</p>
			</CardHeader>
			<CardContent class="space-y-4">
				{#if agreementsQuery.isLoading}
					<LoadingState label="Loading agreement versions" testid="lease-agreements-loading" />
				{:else if agreementsQuery.isError}
					<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="lease-agreements-error">
						<p class="text-sm font-medium text-destructive">Agreement history could not be loaded.</p>
						<Button class="mt-3" variant="outline" size="sm" onclick={() => agreementsQuery.refetch()}>Try again</Button>
					</div>
				{:else if (agreementsQuery.data?.items.length ?? 0) === 0}
					<p class="text-sm text-muted-foreground">No agreement versions yet.</p>
				{:else}
					<div class="divide-y">
						{#each agreementsQuery.data?.items ?? [] as agreement}
							{@const issuedArtifact = agreement.issuedArtifact}
							{@const executedArtifact = agreement.executedArtifact}
							<div class="space-y-3 py-4" data-testid="agreement-version-{agreement.leaseAgreementId}">
								<div class="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
									<div class="space-y-1">
										<div class="flex flex-wrap items-center gap-2">
											<p class="font-medium">{agreement.agreementNumber} · version {agreement.versionNumber}</p>
											<StatusBadge status={agreement.agreementStatus} />
											{#if agreement.isGoverning}<span class="rounded-full bg-primary/10 px-2 py-0.5 text-xs font-medium text-primary">Governing</span>{/if}
										</div>
										<p class="text-sm text-muted-foreground">{agreement.changeType} · {agreement.termStartOn} to {agreement.termEndOn ?? 'month-to-month'} · ${agreement.baseRentAmount.toLocaleString()}/month · {agreement.signerCount} signer{agreement.signerCount === 1 ? '' : 's'}</p>
										{#if agreement.correctionReason}<p class="text-xs text-muted-foreground">Correction reason: {agreement.correctionReason}</p>{/if}
										{#if agreement.reissueReason}<p class="text-xs text-muted-foreground">Reissue reason: {agreement.reissueReason}</p>{/if}
										{#if agreement.draftCancellationReason}<p class="text-xs text-destructive">Draft canceled: {agreement.draftCancellationReason}</p>{/if}
									</div>
									<div class="flex flex-wrap gap-2">
										{#if canPrepareAgreements && agreement.agreementStatus === 'Draft'}
											<Button size="sm" class="gap-2" onclick={() => agreement.hasSourceScan ? downloadSourceScan(agreement) : openAgreementDraft(agreement)}>
												{#if agreement.hasSourceScan}<Eye class="h-4 w-4" /> View draft{:else}<FilePenLine class="h-4 w-4" /> Edit draft{/if}
											</Button>
										{:else if agreement.hasSourceScan}
											<Button size="sm" variant="outline" class="gap-2" onclick={() => downloadSourceScan(agreement)}><ScanLine class="h-4 w-4" /> {agreement.agreementStatus === 'Draft' ? 'View draft' : 'Source scan'}</Button>
										{/if}
										{#if issuedArtifact}
											<Button size="sm" variant="outline" onclick={() => (signatureProgressAgreementId = signatureProgressAgreementId === agreement.leaseAgreementId ? null : agreement.leaseAgreementId)}>
												{signatureProgressAgreementId === agreement.leaseAgreementId ? 'Hide signing progress' : 'View signing progress'}
											</Button>
										{/if}
										{#if canPrepareAgreements && issuedArtifact && !agreement.fullyExecutedAtUtc && !agreement.hasLiveReissue}
											<Button size="sm" variant={agreement.voidedAtUtc ? 'default' : 'destructive'} onclick={() => (issuedRecoverySource = agreement)}>
												{agreement.voidedAtUtc ? 'Create replacement' : 'Void and replace'}
											</Button>
										{/if}
									</div>
								</div>

								{#if signatureProgressAgreementId === agreement.leaseAgreementId}
									<AgreementSignatureProgress leaseManagementId={leaseManagementId} leaseAgreementId={agreement.leaseAgreementId} agreementNumber={agreement.agreementNumber} onclose={() => (signatureProgressAgreementId = null)} />
								{/if}

								<div class="flex flex-wrap gap-2">
									{#if issuedArtifact}
										<Button variant="outline" size="sm" class="gap-2" onclick={() => downloadAgreement(agreement.leaseAgreementId, issuedArtifact.legalDocumentArtifactId, issuedArtifact.fileName)}><FileDown class="h-4 w-4" /> Issued PDF</Button>
									{/if}
									{#if executedArtifact}
										<Button variant="outline" size="sm" class="gap-2" onclick={() => downloadAgreement(agreement.leaseAgreementId, executedArtifact.legalDocumentArtifactId, executedArtifact.fileName)}><FileDown class="h-4 w-4" /> Executed PDF</Button>
									{/if}
								</div>

				{#if canPrepareAgreements && agreement.isGoverning}
									<div class="flex flex-wrap items-center gap-2 rounded-xl border bg-muted/20 p-3">
										<span class="mr-1 text-xs font-medium uppercase tracking-wide text-muted-foreground">Create next draft</span>
										<Button size="sm" onclick={() => (createAddendumBase = agreement)}><FilePlus2 class="mr-1 h-4 w-4" /> Addendum</Button>
										<Button variant="outline" size="sm" onclick={() => (successorSelection = { source: agreement, changeType: 'Correction' })}>Create correction</Button>
										<Button variant="outline" size="sm" onclick={() => (successorSelection = { source: agreement, changeType: 'Restatement' })}>Create restatement</Button>
										<Button variant="outline" size="sm" onclick={() => (successorSelection = { source: agreement, changeType: 'Renewal' })}>Create renewal</Button>
										<Button variant="outline" size="sm" onclick={() => (successorSelection = { source: agreement, changeType: 'MonthToMonth' })}>Create month-to-month</Button>
									</div>
								{/if}
							</div>
						{/each}
					</div>
				{/if}

				{#if agreementsQuery.data}
					<div class="flex flex-col gap-2 border-t pt-4">
						<p class="text-xs text-muted-foreground">{agreementsQuery.data.totalCount} agreement version{agreementsQuery.data.totalCount === 1 ? '' : 's'} total</p>
						<Pagination bind:skip={agreementSkip} take={AGREEMENT_PAGE_SIZE} count={agreementsQuery.data.items.length} hasNext={agreementSkip + agreementsQuery.data.items.length < agreementsQuery.data.totalCount} testid="agreement-history-pagination" />
					</div>
				{/if}
			</CardContent>
		</Card>

		<Card>
			<CardHeader>
				<div class="flex flex-wrap items-start justify-between gap-3">
					<div><CardTitle>Addendum versions</CardTitle><p class="mt-1 text-sm text-muted-foreground">Server-paged draft, signature, correction-series, financial-effect, and immutable artifact history.</p></div>
					{#if canPrepareAgreements}
						<Button size="sm" class="gap-2" disabled={!governingAgreement} title={governingAgreement ? `Create an addendum to ${governingAgreement.agreementNumber}` : 'A governing agreement is required'} onclick={() => { if (governingAgreement) createAddendumBase = governingAgreement; }}><FilePlus2 class="h-4 w-4" /> New draft</Button>
					{/if}
				</div>
			</CardHeader>
			<CardContent class="space-y-4">
				{#if addendaQuery.isLoading}
					<LoadingState label="Loading addendum versions" testid="lease-addenda-loading" />
				{:else if addendaQuery.isError}
					<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="lease-addenda-error">
						<p class="text-sm font-medium text-destructive">Addendum history could not be loaded.</p>
						<Button class="mt-3" variant="outline" size="sm" onclick={() => addendaQuery.refetch()}>Try again</Button>
					</div>
				{:else if (addendaQuery.data?.items.length ?? 0) === 0}
					<p class="text-sm text-muted-foreground">No addendum versions yet.</p>
				{:else}
					<div class="divide-y">
						{#each addendaQuery.data?.items ?? [] as addendum (addendum.leaseAddendumId)}
							{@const issuedArtifact = addendum.issuedArtifact}
							{@const executedArtifact = addendum.executedArtifact}
							<div class="space-y-3 py-4" data-testid="addendum-version-{addendum.leaseAddendumId}">
								<div class="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
									<div class="space-y-1">
										<div class="flex flex-wrap items-center gap-2">
											<p class="font-medium">{addendum.addendumNumber} · version {addendum.versionNumber}</p>
											<StatusBadge status={addendum.addendumStatus} />
											<span class="rounded-full bg-muted px-2 py-0.5 text-xs">{addendum.purpose}</span>
										</div>
										<p class="text-sm text-muted-foreground">Effective {addendum.effectiveFromOn}{addendum.effectiveThroughOn ? ` through ${addendum.effectiveThroughOn}` : ' onward'} · series {addendum.seriesPublicId}</p>
										<p class="text-xs text-muted-foreground">{addendum.signerCount} signer{addendum.signerCount === 1 ? '' : 's'} · {addendum.financialEffectCount} financial effect{addendum.financialEffectCount === 1 ? '' : 's'}{addendum.recurringRentDelta ? ` · recurring rent ${addendum.recurringRentDelta > 0 ? '+' : ''}$${addendum.recurringRentDelta.toLocaleString()}` : ''}</p>
									</div>
									<div class="flex flex-wrap gap-2">
						{#if canPrepareAgreements && addendum.addendumStatus === 'Draft'}
											<Button size="sm" class="gap-2" onclick={() => (editAddendumId = addendum.leaseAddendumId)}><FilePenLine class="h-4 w-4" /> Edit draft</Button>
										{/if}
						{#if canPrepareAgreements && executedArtifact && !addendum.supersededEffectiveOn && !addendum.voidedAtUtc}
											<Button variant="outline" size="sm" onclick={() => (correctionAddendum = addendum)}>Correct</Button>
										{/if}
									</div>
								</div>

								<div class="flex flex-wrap gap-2">
									{#if issuedArtifact}
										<Button variant="outline" size="sm" class="gap-2" onclick={() => downloadAddendum(addendum.leaseAddendumId, issuedArtifact.legalDocumentArtifactId, issuedArtifact.fileName)}><FileDown class="h-4 w-4" /> Issued PDF</Button>
									{/if}
									{#if executedArtifact}
										<Button variant="outline" size="sm" class="gap-2" onclick={() => downloadAddendum(addendum.leaseAddendumId, executedArtifact.legalDocumentArtifactId, executedArtifact.fileName)}><FileDown class="h-4 w-4" /> Executed PDF</Button>
									{/if}
								</div>
							</div>
						{/each}
					</div>
				{/if}

				{#if addendaQuery.data}
					<div class="flex flex-col gap-2 border-t pt-4">
						<p class="text-xs text-muted-foreground">{addendaQuery.data.totalCount} addendum version{addendaQuery.data.totalCount === 1 ? '' : 's'} total</p>
						<Pagination bind:skip={addendumSkip} take={ADDENDUM_PAGE_SIZE} count={addendaQuery.data.items.length} hasNext={addendumSkip + addendaQuery.data.items.length < addendaQuery.data.totalCount} testid="addendum-history-pagination" />
					</div>
				{/if}
			</CardContent>
		</Card>
	</div>

	{#if editAgreementId}
		<AgreementDraftDialog leaseManagementId={leaseManagementId} leaseAgreementId={editAgreementId} source={editAgreementSource} canCancel={editAgreementCanCancel} onclose={() => { editAgreementId = null; editAgreementSource = null; editAgreementCanCancel = false; }} onissued={handleIssued} oncanceled={handleDraftCanceled} />
	{/if}
	{#if successorSelection}
		<AgreementSuccessorDialog leaseManagementId={leaseManagementId} source={successorSelection.source} changeType={successorSelection.changeType} businessDate={summary.businessDate} onclose={() => (successorSelection = null)} oncreated={handleSuccessorCreated} />
	{/if}
	{#if issuedRecoverySource}
		<AgreementIssuedRecoveryDialog leaseManagementId={leaseManagementId} source={issuedRecoverySource} onclose={() => (issuedRecoverySource = null)} oncreated={handleIssuedRecoveryCreated} />
	{/if}
	{#if createAddendumBase}
		<AddendumCreateDialog leaseManagementId={leaseManagementId} propertyId={summary.propertyId} baseAgreement={createAddendumBase} onclose={() => (createAddendumBase = null)} oncreated={handleAddendumDraftCreated} />
	{/if}
	{#if editAddendumId}
		<AddendumDraftDialog leaseManagementId={leaseManagementId} leaseAddendumId={editAddendumId} onclose={() => (editAddendumId = null)} onissued={handleAddendumIssued} />
	{/if}
	{#if correctionAddendum}
		<AddendumCorrectionDialog leaseManagementId={leaseManagementId} source={correctionAddendum} onclose={() => (correctionAddendum = null)} oncreated={handleAddendumDraftCreated} />
	{/if}
	{#if householdAction}
		<HouseholdManagementDialog mode={householdAction.mode} {summary} party={householdAction.party} parties={householdContextQuery.data?.parties ?? []} agreements={agreementsQuery.data?.items ?? []} accessId={householdAction.access?.tenantUserAccessId} onclose={() => (householdAction = null)} onchanged={refreshLease} />
	{/if}
	{#if currentNoticeParty && canManageTenantNotices}
		<TenantNoticeDialog
			bind:open={noticeDialogOpen}
			leaseManagementId={leaseManagementId}
			recipientTenantId={currentNoticeParty.tenantId}
			tenantName={currentNoticeParty.tenantName}
			activeLeaseCount={1}
		/>
	{/if}
{/if}
