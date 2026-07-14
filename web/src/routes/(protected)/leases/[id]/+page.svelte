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
	import AddendumCorrectionDialog from '$lib/components/leases/AddendumCorrectionDialog.svelte';
	import AddendumCreateDialog from '$lib/components/leases/AddendumCreateDialog.svelte';
	import AddendumDraftDialog from '$lib/components/leases/AddendumDraftDialog.svelte';
	import AgreementDraftDialog from '$lib/components/leases/AgreementDraftDialog.svelte';
	import AgreementSignatureProgress from '$lib/components/leases/AgreementSignatureProgress.svelte';
	import AgreementSuccessorDialog from '$lib/components/leases/AgreementSuccessorDialog.svelte';
	import EndingDispositionDialog from '$lib/components/leases/EndingDispositionDialog.svelte';
	import HouseholdManagementDialog from '$lib/components/leases/HouseholdManagementDialog.svelte';
	import PossessionActions from '$lib/components/leases/PossessionActions.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Card, CardContent, CardHeader, CardTitle } from '$lib/components/ui/card';
	import { CalendarClock, FileDown, FilePenLine, FilePlus2, Home, ScanLine, Users } from '@lucide/svelte';
	import { apiErrorMessage, showError } from '$lib/utils/toast';
	import type { LeaseManagementParty } from '$lib/types';
	import type { ReturnPossessionActiveTenantUserAccess } from '$lib/api/endpoints/lease-managements';

	type SuccessorType = 'Correction' | 'Restatement' | 'Renewal' | 'MonthToMonth';
	type SuccessorSelection = { source: LeaseAgreementSummary; changeType: SuccessorType };

	const AGREEMENT_PAGE_SIZE = 10;
	const ADDENDUM_PAGE_SIZE = 10;
	const queryClient = useQueryClient();
	const leaseManagementId = $derived(Number(page.params.id));
	let agreementSkip = $state(0);
	let editAgreementId = $state<number | null>(null);
	let editAgreementSource = $state<LeaseAgreementSummary | null>(null);
	let editAgreementCanCancel = $state(false);
	let signatureProgressAgreementId = $state<number | null>(null);
	let successorSelection = $state<SuccessorSelection | null>(null);
	let addendumSkip = $state(0);
	let editAddendumId = $state<number | null>(null);
	let createAddendumBase = $state<LeaseAgreementSummary | null>(null);
	let correctionAddendum = $state<LeaseAddendumHistoryItem | null>(null);
	let endingDispositionOpen = $state(false);
	type HouseholdAction = { mode: 'add' | 'change' | 'end' | 'grant' | 'revoke'; party?: LeaseManagementParty; access?: ReturnPossessionActiveTenantUserAccess };
	let householdAction = $state<HouseholdAction | null>(null);
	const activeExperience = $derived(page.data.access?.selectedContext.activeExperience ?? null);
	const activeCapabilities = $derived(new Set(page.data.access?.navigation.find((entry) => entry.experience === activeExperience)?.capabilityKeys ?? []));
	const canManageHousehold = $derived(activeCapabilities.has('rentals.manage') || activeCapabilities.has('leasing.onboarding.manage'));
	const canPrepareAgreements = $derived(activeCapabilities.has('rentals.manage') || activeCapabilities.has('leasing.agreements.prepare'));

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

	async function handleSuccessorCreated(result: LeaseAgreementDraftMutationResponse) {
		editAgreementSource = successorSelection?.source ?? null;
		editAgreementCanCancel = true;
		successorSelection = null;
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

{#if relationshipQuery.isLoading}
	<p class="text-muted-foreground">Loading tenant and lease relationship…</p>
{:else if relationshipQuery.error || !relationshipQuery.data}
	<div class="rounded-xl border border-destructive/30 bg-destructive/5 p-6">
		<h1 class="text-xl font-semibold">Tenant and lease relationship unavailable</h1>
		<p class="mt-2 text-sm text-muted-foreground">
			It may no longer exist or you may not have access.
		</p>
		<Button href="/leases" variant="outline" class="mt-4">Back to leases</Button>
	</div>
{:else}
	{@const detail = relationshipQuery.data}
	{@const summary = detail.summary}
	<div class="space-y-6">
		<PageHeader
			title={summary.primaryTenantName ?? 'Tenant & lease'}
			description={`${summary.propertyName}${summary.unitNumber ? ` · ${summary.unitNumber}` : ''} · ${summary.relationshipNumber}`}
		>
			{#snippet actions()}
				<Button href={`/units/${summary.unitId}?tab=tenant-lease&view=agreements`} variant="outline" class="gap-2"
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
			<Card>
				<CardHeader><CardTitle class="text-base">Relationship</CardTitle></CardHeader>
				<CardContent class="space-y-2">
					<StatusBadge status={summary.lifecycle} />
					<p class="text-sm text-muted-foreground">
						{summary.possessionGivenAtUtc
							? `Possession given ${new Date(summary.possessionGivenAtUtc).toLocaleDateString()}`
							: summary.plannedPossessionAtUtc
								? `Possession planned ${new Date(summary.plannedPossessionAtUtc).toLocaleDateString()}`
								: 'Possession not yet scheduled'}
					</p>
				</CardContent>
			</Card>
			<Card>
				<CardHeader><CardTitle class="text-base">Governing agreement</CardTitle></CardHeader>
				<CardContent>
					<p class="font-medium">{summary.agreementNumber ?? 'No governing agreement'}</p>
					<p class="text-sm text-muted-foreground">
						{summary.agreementStatus ?? 'Not issued'}{summary.termEndOn ? ` · ends ${summary.termEndOn}` : ''}
					</p>
					{#if summary.upcomingLeaseAgreementId}
						<p class="mt-1 text-xs text-primary">
							Upcoming: {summary.upcomingAgreementNumber ?? `agreement #${summary.upcomingLeaseAgreementId}`}
							{summary.upcomingTermStartOn ? ` from ${summary.upcomingTermStartOn}` : ''}
							{summary.upcomingAgreementStatus ? ` · ${summary.upcomingAgreementStatus}` : ''}
						</p>
					{/if}
				</CardContent>
			</Card>
			<Card>
				<CardHeader><CardTitle class="text-base">Tenant account</CardTitle></CardHeader>
				<CardContent>
					<p class="font-medium">{summary.tenantAccountId ? `Account #${summary.tenantAccountId}` : 'Not opened'}</p>
					<p class="text-sm text-muted-foreground">Continues across renewals and corrections</p>
				</CardContent>
			</Card>
			<Card>
				<CardHeader><CardTitle class="text-base">Ending plan</CardTitle></CardHeader>
				<CardContent class="space-y-2">
					<p class="font-medium">{endingDispositionLabel(summary.endingDisposition)}</p>
					{#if summary.endingDispositionDecidedAtUtc}
						<p class="text-xs text-muted-foreground">Decided {new Date(summary.endingDispositionDecidedAtUtc).toLocaleDateString()}</p>
					{/if}
					{#if summary.plannedMoveOutAtUtc}
						<p class="text-sm text-muted-foreground">Move-out planned {new Date(summary.plannedMoveOutAtUtc).toLocaleDateString()}</p>
					{/if}
					{#if canPrepareAgreements && summary.possessionGivenAtUtc && !summary.possessionReturnedAtUtc && !summary.canceledAtUtc}
						<Button variant="outline" size="sm" class="gap-2" onclick={() => (endingDispositionOpen = true)}>
							<CalendarClock class="h-4 w-4" /> Record decision
						</Button>
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
				{#if detail.parties.length === 0}
					<p class="text-sm text-muted-foreground">No effective parties.</p>
				{:else}
					<div>
						<h3 class="text-sm font-semibold">Current household</h3>
						<div class="divide-y">
						{#each householdContextQuery.data?.parties ?? [] as party}
							{@const access = activeAccess(party.leaseManagementPartyId)}
							<div class="flex flex-col gap-3 py-3 sm:flex-row sm:items-center sm:justify-between">
								<div>
									<p class="font-medium">{party.tenantName}</p>
									<p class="text-sm text-muted-foreground">{party.email ?? party.phone ?? 'No contact information'}</p>
									<p class="text-xs text-muted-foreground">Effective since {party.effectiveFrom} · Login {access ? `active for ${access.userEmail}` : 'not granted'}</p>
								</div>
								<div class="flex flex-wrap items-center gap-2"><StatusBadge status={party.role} />{#if canManageHousehold}<Button size="sm" variant="outline" onclick={() => (householdAction = { mode: 'change', party })}>Change role</Button><Button size="sm" variant="outline" onclick={() => (householdAction = { mode: 'end', party })}>End</Button>{#if access}<Button size="sm" variant="outline" onclick={() => (householdAction = { mode: 'revoke', party, access })}>Revoke login</Button>{:else}<Button size="sm" variant="outline" disabled={!party.email} title={party.email ? 'Create relationship-scoped resident login' : 'Add an email to this person first'} onclick={() => (householdAction = { mode: 'grant', party })}>Create login</Button>{/if}{/if}</div>
							</div>
						{/each}
						</div>
					</div>
					<div>
						<h3 class="text-sm font-semibold">Household history</h3>
						<div class="divide-y">
						{#each detail.parties as party}
							{#if !party.isCurrent}<div class="flex items-center justify-between gap-4 py-3"><div><p class="font-medium">{party.tenantName}</p><p class="text-xs text-muted-foreground">{party.effectiveFrom} through {party.effectiveThrough ?? 'current'}</p></div><StatusBadge status={party.role} /></div>{/if}
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
					<p class="text-sm text-muted-foreground">Loading agreement versions…</p>
				{:else if agreementsQuery.isError}
					<p class="text-sm text-destructive">Agreement history could not be loaded.</p>
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
										{#if agreement.draftCancellationReason}<p class="text-xs text-destructive">Draft canceled: {agreement.draftCancellationReason}</p>{/if}
									</div>
									<div class="flex flex-wrap gap-2">
						{#if canPrepareAgreements && agreement.agreementStatus === 'Draft'}
											<Button size="sm" class="gap-2" onclick={() => openAgreementDraft(agreement)}><FilePenLine class="h-4 w-4" /> Edit draft</Button>
										{/if}
										{#if agreement.hasSourceScan}
											<Button size="sm" variant="outline" class="gap-2" onclick={() => downloadSourceScan(agreement)}><ScanLine class="h-4 w-4" /> Source scan</Button>
										{/if}
										{#if issuedArtifact}
											<Button size="sm" variant="outline" onclick={() => (signatureProgressAgreementId = signatureProgressAgreementId === agreement.leaseAgreementId ? null : agreement.leaseAgreementId)}>
												{signatureProgressAgreementId === agreement.leaseAgreementId ? 'Hide signing progress' : 'View signing progress'}
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
										<Button variant="outline" size="sm" onclick={() => (successorSelection = { source: agreement, changeType: 'Correction' })}>Correct</Button>
										<Button variant="outline" size="sm" onclick={() => (successorSelection = { source: agreement, changeType: 'Restatement' })}>Restate</Button>
										<Button variant="outline" size="sm" onclick={() => (successorSelection = { source: agreement, changeType: 'Renewal' })}>Renew</Button>
										<Button variant="outline" size="sm" onclick={() => (successorSelection = { source: agreement, changeType: 'MonthToMonth' })}>Month-to-month</Button>
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
				<CardTitle>Addendum versions</CardTitle>
				<p class="text-sm text-muted-foreground">Server-paged draft, signature, correction-series, financial-effect, and immutable artifact history.</p>
			</CardHeader>
			<CardContent class="space-y-4">
				{#if addendaQuery.isLoading}
					<p class="text-sm text-muted-foreground">Loading addendum versions…</p>
				{:else if addendaQuery.isError}
					<p class="text-sm text-destructive">Addendum history could not be loaded.</p>
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
{/if}
