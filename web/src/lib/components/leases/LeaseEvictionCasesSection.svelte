<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		evictionCases,
		type CreateEvictionCaseEventRequest,
		type CreateEvictionCaseRequest,
		type EvictionCaseStatus,
		type EvictionEventType
	} from '$lib/api/endpoints/eviction-cases';
	import type { LeaseManagementParty } from '$lib/types';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import SimpleSelect from '$lib/components/shared/SimpleSelect.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Card, CardContent, CardHeader, CardTitle } from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Input } from '$lib/components/ui/input';
	import { formatDateOnly } from '$lib/utils/date';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Gavel, Loader2, Plus } from '@lucide/svelte';

	const PAGE_SIZE = 10;
	const statusOptions: Array<{ value: EvictionCaseStatus; label: string }> = [
		{ value: 'Draft', label: 'Preparing' },
		{ value: 'NoticeServed', label: 'Notice delivered' },
		{ value: 'Filed', label: 'Filed with the court' },
		{ value: 'HearingScheduled', label: 'Hearing scheduled' },
		{ value: 'Judgment', label: 'Decision entered' },
		{ value: 'MoveOut', label: 'Move-out recorded' },
		{ value: 'Settled', label: 'Settled' },
		{ value: 'Dismissed', label: 'Dismissed' }
	];
	const eventOptions: Array<{ value: EvictionEventType; label: string }> = [
		{ value: 'NoticeServed', label: 'Notice delivered' },
		{ value: 'Filed', label: 'Filed with the court' },
		{ value: 'HearingScheduled', label: 'Hearing scheduled' },
		{ value: 'Judgment', label: 'Court decision entered' },
		{ value: 'MoveOut', label: 'Move-out recorded' },
		{ value: 'Settlement', label: 'Settlement reached' },
		{ value: 'Dismissal', label: 'Case dismissed' },
		{ value: 'PaymentPlan', label: 'Payment plan agreed' },
		{ value: 'Note', label: 'Other update' }
	];

	let {
		leaseManagementId,
		leaseAgreementId = null,
		respondents,
		canManage
	}: {
		leaseManagementId: number;
		leaseAgreementId?: number | null;
		respondents: LeaseManagementParty[];
		canManage: boolean;
	} = $props();

	const queryClient = useQueryClient();
	let skip = $state(0);
	let createOpen = $state(false);
	let createStatus = $state<EvictionCaseStatus>('Filed');
	let selectedRespondentIds = $state<number[]>([]);
	let filedOnDate = $state('');
	let hearingDate = $state('');
	let courtName = $state('');
	let caseNumber = $state('');
	let caseNotes = $state('');
	let createValidationError = $state('');
	let statusDrafts = $state<Record<number, EvictionCaseStatus>>({});
	let eventCaseId = $state<number | null>(null);
	let eventType = $state<EvictionEventType>('Note');
	let eventDate = $state('');
	let eventNotes = $state('');
	let eventValidationError = $state('');

	const casesQuery = createQuery(() => ({
		queryKey: ['eviction-cases', leaseManagementId, skip],
		queryFn: () =>
			evictionCases.listPage({
				leaseManagementId,
				skip,
				take: PAGE_SIZE,
				sort: '-updatedAt'
			}),
		enabled: Number.isInteger(leaseManagementId) && leaseManagementId > 0
	}));

	function utcDate(value: string) {
		return `${value}T12:00:00.000Z`;
	}

	function statusLabel(status: EvictionCaseStatus) {
		return statusOptions.find((option) => option.value === status)?.label ?? status;
	}

	function resetCreateForm() {
		createStatus = 'Filed';
		selectedRespondentIds = [];
		filedOnDate = '';
		hearingDate = '';
		courtName = '';
		caseNumber = '';
		caseNotes = '';
		createValidationError = '';
	}

	function openCreate() {
		resetCreateForm();
		createOpen = true;
	}

	function closeCreate() {
		if (createCaseMutation.isPending) return;
		createOpen = false;
		createValidationError = '';
	}

	function setRespondentSelected(partyId: number, checked: boolean) {
		if (checked) {
			if (!selectedRespondentIds.includes(partyId)) {
				selectedRespondentIds = [...selectedRespondentIds, partyId];
			}
			return;
		}
		selectedRespondentIds = selectedRespondentIds.filter((id) => id !== partyId);
	}

	function buildCreateRequest(): CreateEvictionCaseRequest | null {
		createValidationError = '';
		if (selectedRespondentIds.length === 0) {
			createValidationError = 'Choose at least one person named in the case.';
			return null;
		}
		return {
			leaseManagementId,
			leaseAgreementId: leaseAgreementId && leaseAgreementId > 0 ? leaseAgreementId : null,
			respondentLeaseManagementPartyIds: selectedRespondentIds,
			status: createStatus,
			filedOnDate: filedOnDate ? utcDate(filedOnDate) : null,
			hearingDate: hearingDate ? utcDate(hearingDate) : null,
			courtName: courtName.trim() || null,
			caseNumber: caseNumber.trim() || null,
			notes: caseNotes.trim() || null
		};
	}

	async function refreshCases() {
		await queryClient.invalidateQueries({ queryKey: ['eviction-cases', leaseManagementId] });
	}

	const createCaseMutation = createMutation(() => ({
		mutationFn: (request: CreateEvictionCaseRequest) => evictionCases.create(request),
		onSuccess: async () => {
			showSuccess('Eviction case created.');
			createOpen = false;
			resetCreateForm();
			skip = 0;
			await refreshCases();
		},
		onError: (error) => showError(apiErrorMessage(error, 'The eviction case could not be created.'))
	}));

	function submitCreate() {
		const request = buildCreateRequest();
		if (request) createCaseMutation.mutate(request);
	}

	function setStatusDraft(id: number, value: string) {
		statusDrafts = { ...statusDrafts, [id]: value as EvictionCaseStatus };
	}

	const statusMutation = createMutation(() => ({
		mutationFn: ({ id, status }: { id: number; status: EvictionCaseStatus }) =>
			evictionCases.update(id, { status }),
		onSuccess: async (_updated, variables) => {
			const { [variables.id]: _removed, ...remaining } = statusDrafts;
			statusDrafts = remaining;
			showSuccess('Case status updated.');
			await refreshCases();
		},
		onError: (error) => showError(apiErrorMessage(error, 'The case status could not be updated.'))
	}));

	function openEvent(id: number) {
		eventCaseId = id;
		eventType = 'Note';
		eventDate = '';
		eventNotes = '';
		eventValidationError = '';
	}

	function closeEvent() {
		if (eventMutation.isPending) return;
		eventCaseId = null;
		eventValidationError = '';
	}

	function buildEventRequest(): CreateEvictionCaseEventRequest | null {
		eventValidationError = '';
		if (!eventDate) {
			eventValidationError = 'Choose the date this happened.';
			return null;
		}
		return {
			eventType,
			eventDate: utcDate(eventDate),
			notes: eventNotes.trim() || null
		};
	}

	const eventMutation = createMutation(() => ({
		mutationFn: ({ id, request }: { id: number; request: CreateEvictionCaseEventRequest }) =>
			evictionCases.addEvent(id, {
				eventType: request.eventType,
				eventDate: request.eventDate,
				notes: request.notes
			}),
		onSuccess: async () => {
			showSuccess('Case update added.');
			eventCaseId = null;
			await refreshCases();
		},
		onError: (error) => showError(apiErrorMessage(error, 'The case update could not be added.'))
	}));

	function submitEvent() {
		const request = buildEventRequest();
		if (eventCaseId && request) eventMutation.mutate({ id: eventCaseId, request });
	}
</script>

<Card data-testid="lease-eviction-cases">
	<CardHeader>
		<div class="flex flex-wrap items-start justify-between gap-3">
			<div>
				<CardTitle class="flex items-center gap-2"><Gavel class="h-5 w-5" /> Eviction cases</CardTitle>
				<p class="mt-1 text-sm text-muted-foreground">
					Track court filings, hearing dates, and important updates for this tenant relationship.
				</p>
			</div>
			{#if canManage}
				<Button size="sm" class="gap-2" onclick={openCreate} data-testid="eviction-case-create">
					<Plus class="h-4 w-4" /> New case
				</Button>
			{/if}
		</div>
	</CardHeader>
	<CardContent class="space-y-4">
		{#if casesQuery.isLoading}
			<LoadingState label="Loading eviction cases" testid="eviction-cases-loading" />
		{:else if casesQuery.isError}
			<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="eviction-cases-error">
				<p class="text-sm font-medium text-destructive">Eviction cases could not be loaded.</p>
				<Button class="mt-3" variant="outline" size="sm" onclick={() => casesQuery.refetch()}>Try again</Button>
			</div>
		{:else if (casesQuery.data?.items.length ?? 0) === 0}
			<p class="text-sm text-muted-foreground">No eviction cases have been recorded for this tenant relationship.</p>
		{:else}
			<div class="divide-y">
				{#each casesQuery.data?.items ?? [] as caseItem (caseItem.id)}
					{@const selectedStatus = statusDrafts[caseItem.id] ?? caseItem.status}
					<article class="space-y-3 py-4" data-testid={caseItem.testId}>
						<div class="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
							<div class="space-y-1">
								<div class="flex flex-wrap items-center gap-2">
									<h3 class="font-medium">{caseItem.caseNumber ? `Court case ${caseItem.caseNumber}` : `Case ${caseItem.id}`}</h3>
									<span class="rounded-full bg-muted px-2 py-0.5 text-xs font-medium">{statusLabel(caseItem.status)}</span>
								</div>
								<p class="text-sm text-muted-foreground">
									{caseItem.respondents.map((party) => party.tenantName).join(', ')}
									· {caseItem.agreementNumber ? `Agreement ${caseItem.agreementNumber}` : 'No agreement linked'}
								</p>
								<p class="text-sm text-muted-foreground">
									{caseItem.courtName ?? 'Court not entered'}
									· Filed {caseItem.filedOnDate ? formatDateOnly(caseItem.filedOnDate) : 'date not entered'}
									· Hearing {caseItem.hearingDate ? formatDateOnly(caseItem.hearingDate) : 'not scheduled'}
								</p>
								<p class="text-xs text-muted-foreground">
									{caseItem.eventCount} update{caseItem.eventCount === 1 ? '' : 's'}
									{caseItem.latestEventDate ? ` · Latest ${formatDateOnly(caseItem.latestEventDate)}` : ''}
								</p>
								{#if caseItem.notes}<p class="text-sm">{caseItem.notes}</p>{/if}
							</div>
							<div class="flex flex-col gap-2 sm:min-w-56">
								{#if canManage}
									<SimpleSelect
										value={selectedStatus}
										onchange={(value) => setStatusDraft(caseItem.id, value)}
										options={statusOptions}
										ariaLabel="Case status"
										testid="eviction-case-status-{caseItem.id}"
									/>
									<Button
										size="sm"
										variant="outline"
										disabled={statusMutation.isPending || selectedStatus === caseItem.status}
										onclick={() => statusMutation.mutate({ id: caseItem.id, status: selectedStatus })}
									>
										{#if statusMutation.isPending}<Loader2 class="mr-2 h-4 w-4 animate-spin" />{/if}
										Save status
									</Button>
								{/if}
								{#if canManage}
									<Button
										size="sm"
										variant="ghost"
										onclick={() => openEvent(caseItem.id)}
										data-testid="eviction-case-add-event-{caseItem.id}"
									>
										Add update
									</Button>
								{/if}
							</div>
						</div>
					</article>
				{/each}
			</div>
		{/if}

		{#if casesQuery.data}
			<div class="flex flex-col gap-2 border-t pt-4">
				<p class="text-xs text-muted-foreground">
					{casesQuery.data.totalCount} case{casesQuery.data.totalCount === 1 ? '' : 's'} total
				</p>
				<Pagination
					bind:skip={skip}
					take={PAGE_SIZE}
					count={casesQuery.data.items.length}
					hasNext={skip + casesQuery.data.items.length < casesQuery.data.totalCount}
					testid="eviction-cases-pagination"
				/>
			</div>
		{/if}
	</CardContent>
</Card>

{#if createOpen && canManage}
	<Dialog.Root open onOpenChange={(open) => { if (!open) closeCreate(); }}>
		<Dialog.Content class="max-h-[90vh] max-w-2xl overflow-y-auto">
			<Dialog.Header>
				<Dialog.Title>Create an eviction case</Dialog.Title>
				<Dialog.Description>
					Add the people named in the case and any court details you already have.
				</Dialog.Description>
			</Dialog.Header>
			<div class="space-y-4 py-2">
				<div class="space-y-2">
					<p class="text-sm font-medium">People named in the case</p>
					{#if respondents.length === 0}
						<p class="rounded-md border p-3 text-sm text-muted-foreground">No people are attached to this tenant relationship.</p>
					{:else}
						<div class="divide-y rounded-md border">
							{#each respondents as party (party.leaseManagementPartyId)}
								<label class="flex cursor-pointer items-center gap-3 px-3 py-2 text-sm">
									<input
										type="checkbox"
										checked={selectedRespondentIds.includes(party.leaseManagementPartyId)}
										onchange={(event) => setRespondentSelected(party.leaseManagementPartyId, event.currentTarget.checked)}
										data-testid="eviction-case-respondent-{party.leaseManagementPartyId}"
									/>
									<span class="flex-1">{party.tenantName}</span>
									<span class="text-xs text-muted-foreground">{party.isCurrent ? 'Current household' : 'Past household member'}</span>
								</label>
							{/each}
						</div>
					{/if}
				</div>
				<label class="space-y-1 text-sm">
					<span class="font-medium">Case status</span>
					<SimpleSelect value={createStatus} onchange={(value) => (createStatus = value as EvictionCaseStatus)} options={statusOptions} />
				</label>
				<div class="grid gap-3 sm:grid-cols-2">
					<label class="space-y-1 text-sm"><span class="font-medium">Filed date (optional)</span><DatePicker bind:value={filedOnDate} /></label>
					<label class="space-y-1 text-sm"><span class="font-medium">Hearing date (optional)</span><DatePicker bind:value={hearingDate} /></label>
					<label class="space-y-1 text-sm"><span class="font-medium">Court name (optional)</span><Input bind:value={courtName} maxlength={200} /></label>
					<label class="space-y-1 text-sm"><span class="font-medium">Court case number (optional)</span><Input bind:value={caseNumber} maxlength={100} /></label>
				</div>
				<label class="space-y-1 text-sm">
					<span class="font-medium">Notes (optional)</span>
					<textarea bind:value={caseNotes} rows="3" maxlength="1000" class="w-full rounded-md border bg-background px-3 py-2"></textarea>
				</label>
				{#if leaseAgreementId}
					<p class="text-xs text-muted-foreground">This case will be linked to the current agreement.</p>
				{/if}
				{#if createValidationError}<p class="text-sm text-destructive">{createValidationError}</p>{/if}
			</div>
			<Dialog.Footer>
				<Button variant="outline" onclick={closeCreate} disabled={createCaseMutation.isPending}>Cancel</Button>
				<Button onclick={submitCreate} disabled={createCaseMutation.isPending || respondents.length === 0}>
					{#if createCaseMutation.isPending}<Loader2 class="mr-2 h-4 w-4 animate-spin" />{/if}
					Create case
				</Button>
			</Dialog.Footer>
		</Dialog.Content>
	</Dialog.Root>
{/if}

{#if eventCaseId && canManage}
	<Dialog.Root open onOpenChange={(open) => { if (!open) closeEvent(); }}>
		<Dialog.Content class="max-w-lg">
			<Dialog.Header>
				<Dialog.Title>Add a case update</Dialog.Title>
				<Dialog.Description>Record what happened and when.</Dialog.Description>
			</Dialog.Header>
			<div class="space-y-4 py-2">
				<label class="space-y-1 text-sm">
					<span class="font-medium">What happened?</span>
					<SimpleSelect value={eventType} onchange={(value) => (eventType = value as EvictionEventType)} options={eventOptions} />
				</label>
				<label class="space-y-1 text-sm"><span class="font-medium">Date</span><DatePicker bind:value={eventDate} /></label>
				<label class="space-y-1 text-sm">
					<span class="font-medium">Notes (optional)</span>
					<textarea bind:value={eventNotes} rows="3" maxlength="1000" class="w-full rounded-md border bg-background px-3 py-2"></textarea>
				</label>
				{#if eventValidationError}<p class="text-sm text-destructive">{eventValidationError}</p>{/if}
			</div>
			<Dialog.Footer>
				<Button variant="outline" onclick={closeEvent} disabled={eventMutation.isPending}>Cancel</Button>
				<Button onclick={submitEvent} disabled={eventMutation.isPending}>
					{#if eventMutation.isPending}<Loader2 class="mr-2 h-4 w-4 animate-spin" />{/if}
					Add update
				</Button>
			</Dialog.Footer>
		</Dialog.Content>
	</Dialog.Root>
{/if}
