<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import {
		leaseManagements,
		type CreateLeaseAgreementSuccessorDraftRequest,
		type LeaseAgreementDraftMutationResponse,
		type LeaseAgreementRenewalFinancialEffectSummary,
		type LeaseRenewalAddendumDecisionType
	} from '$lib/api/endpoints/lease-managements';
	import type { LeaseAgreementSummary } from '$lib/types';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Input } from '$lib/components/ui/input';
	import { FilePlus2, Loader2 } from '@lucide/svelte';

	type SuccessorType = 'Correction' | 'Restatement' | 'Renewal' | 'MonthToMonth';

	let {
		leaseManagementId,
		source,
		changeType,
		businessDate,
		onclose,
		oncreated
	}: {
		leaseManagementId: number;
		source: LeaseAgreementSummary;
		changeType: SuccessorType;
		businessDate: string;
		onclose: () => void;
		oncreated: (result: LeaseAgreementDraftMutationResponse) => void;
	} = $props();

	const isReplacement = changeType === 'Correction' || changeType === 'Restatement';
	const isRenewal = changeType === 'Renewal' || changeType === 'MonthToMonth';
	function dayAfter(value: string) {
		const date = new Date(`${value}T00:00:00Z`);
		date.setUTCDate(date.getUTCDate() + 1);
		return date.toISOString().slice(0, 10);
	}
	const initialReplacementGoverningDate = businessDate > source.governingFromOn
		? businessDate
		: dayAfter(source.governingFromOn);
	let termStartOn = $state(isReplacement ? source.termStartOn : '');
	let termEndOn = $state(isReplacement ? (source.termEndOn ?? '') : '');
	let governingFromOn = $state(isReplacement ? initialReplacementGoverningDate : '');
	let correctionReason = $state('');
	let validationError = $state('');
	let selectedAddendumDecisions = $state<Record<string, LeaseRenewalAddendumDecisionType | undefined>>({});
	let operation: { fingerprint: string; key: string } | null = null;

	const title = $derived(
		changeType === 'MonthToMonth' ? 'Create month-to-month draft' : `Create ${changeType.toLowerCase()} draft`
	);
	const effectiveSeriesQuery = createQuery(() => ({
		queryKey: [
			'lease-managements',
			leaseManagementId,
			'agreements',
			source.leaseAgreementId,
			'effective-addendum-series'
		],
		queryFn: () => leaseManagements.getEffectiveAddendumSeries(leaseManagementId, source.leaseAgreementId),
		enabled: isRenewal
	}));

	function setAddendumDecision(seriesPublicId: string, event: Event) {
		const value = (event.currentTarget as HTMLSelectElement).value as LeaseRenewalAddendumDecisionType | '';
		selectedAddendumDecisions = {
			...selectedAddendumDecisions,
			[seriesPublicId]: value || undefined
		};
		validationError = '';
	}

	function decisionConsequence(decision: LeaseRenewalAddendumDecisionType | undefined) {
		switch (decision) {
			case 'End':
				return 'Stops this addendum when the new agreement begins. Its separate terms and financial effects do not carry forward.';
			case 'IncorporateIntoBase':
				return 'Records that these terms will be folded into the new base agreement. No replacement addendum is created.';
			case 'ReissueAsAddendum':
				return 'Creates a new editable addendum draft for the new agreement, copying this series\' terms, signers, and financial effects.';
			default:
				return 'Choose how this addendum series should be handled in the new agreement.';
		}
	}

	function effectTypeLabel(effectType: LeaseAgreementRenewalFinancialEffectSummary['effectType']) {
		switch (effectType) {
			case 'RecurringRentDelta': return 'Recurring rent change';
			case 'OneTimeCharge': return 'One-time charge';
			case 'DepositObligationDelta': return 'Deposit obligation change';
		}
	}

	function effectTiming(effect: LeaseAgreementRenewalFinancialEffectSummary) {
		if (effect.dueOn) return `Due ${effect.dueOn}`;
		if (effect.effectiveFromOn) {
			return `Effective ${effect.effectiveFromOn}${effect.effectiveThroughOn ? ` through ${effect.effectiveThroughOn}` : ' onward'}`;
		}
		return 'No separate effective date';
	}

	function buildAddendumDecisions(): CreateLeaseAgreementSuccessorDraftRequest['addendumDecisions'] | null {
		if (!isRenewal) return [];
		const effectiveSeries = effectiveSeriesQuery.data;
		if (!effectiveSeries) {
			validationError = effectiveSeriesQuery.isError
				? 'Could not load the effective addendum series. Retry before creating the draft.'
				: 'Wait for the effective addendum series to load before creating the draft.';
			return null;
		}

		const decisions: CreateLeaseAgreementSuccessorDraftRequest['addendumDecisions'] = [];
		for (const series of effectiveSeries.series) {
			if (!series.decisionRequired) continue;
			const decision = selectedAddendumDecisions[series.seriesPublicId];
			if (!decision) {
				validationError = `Choose what happens to ${series.title} before creating the draft.`;
				return null;
			}
			decisions.push({
				sourceAddendumSeriesPublicId: series.seriesPublicId,
				decision
			});
		}
		return decisions;
	}

	function buildRequest(): CreateLeaseAgreementSuccessorDraftRequest | null {
		validationError = '';
		if (changeType === 'Correction' && !correctionReason.trim()) {
			validationError = 'Explain what is being corrected before creating the draft.';
			return null;
		}
		if (correctionReason.trim().length > 1000) {
			validationError = 'The correction reason cannot exceed 1,000 characters.';
			return null;
		}
		if (!termStartOn || !governingFromOn) {
			validationError = 'Term start and governing date are required.';
			return null;
		}
		if (isReplacement && governingFromOn <= source.governingFromOn) {
			validationError = 'A correction or restatement must begin governing after the current version.';
			return null;
		}
		if (isRenewal && governingFromOn !== termStartOn) {
			validationError = 'A renewal must begin governing on its term start date.';
			return null;
		}
		if (isRenewal && source.termEndOn && termStartOn <= source.termEndOn) {
			validationError = 'The successor term must start after the current term ends.';
			return null;
		}
		if (changeType === 'Renewal' && (!termEndOn || termEndOn < termStartOn)) {
			validationError = 'A fixed-term renewal needs an end date on or after its start.';
			return null;
		}
		const addendumDecisions = buildAddendumDecisions();
		if (!addendumDecisions) return null;
		return {
			changeType,
			correctionReason: changeType === 'Correction' ? correctionReason.trim() : null,
			termStartOn: isReplacement ? source.termStartOn : termStartOn,
			termEndOn:
				changeType === 'MonthToMonth'
					? null
					: isReplacement
						? (source.termEndOn ?? null)
						: termEndOn,
			governingFromOn,
			addendumDecisions
		};
	}

	function operationKey(request: CreateLeaseAgreementSuccessorDraftRequest) {
		const fingerprint = JSON.stringify(request);
		if (!operation || operation.fingerprint !== fingerprint) {
			operation = { fingerprint, key: crypto.randomUUID() };
		}
		return operation.key;
	}

	const mutation = createMutation(() => ({
		mutationFn: (request: CreateLeaseAgreementSuccessorDraftRequest) =>
			leaseManagements.createAgreementSuccessorDraft(
				leaseManagementId,
				source.leaseAgreementId,
				request,
				operationKey(request)
			),
		onSuccess: (result) => {
			showSuccess(`${changeType === 'MonthToMonth' ? 'Month-to-month' : changeType} draft created.`);
			oncreated(result);
		},
		onError: (error) => showError(apiErrorMessage(error, 'Could not create the successor draft.'))
	}));

	function submit() {
		const request = buildRequest();
		if (request) mutation.mutate(request);
	}
</script>

<Dialog.Root open onOpenChange={(open) => { if (!open && !mutation.isPending) onclose(); }}>
	<Dialog.Content class="max-h-[90vh] max-w-2xl overflow-y-auto" data-testid="agreement-successor-dialog">
		<Dialog.Header>
			<Dialog.Title>{title}</Dialog.Title>
			<Dialog.Description>
				Creates a new editable Agreement version. The old agreement keeps governing until the replacement is fully signed and executed.
			</Dialog.Description>
		</Dialog.Header>

		<div class="space-y-4">
			<div class="rounded-xl border bg-muted/20 p-4 text-sm">
				<p class="font-medium">From {source.agreementNumber} · version {source.versionNumber}</p>
				<p class="text-muted-foreground">{source.termStartOn} to {source.termEndOn ?? 'month-to-month'}</p>
			</div>

			{#if isReplacement}
				<div class="rounded-xl border p-4 text-sm text-muted-foreground">
					The source term dates are copied exactly. After creation, open the new draft to edit the corrected terms and signer snapshots.
				</div>
			{:else}
				<label class="space-y-1">
					<span class="text-sm font-medium">New term starts</span>
					<Input type="date" bind:value={termStartOn} data-testid="agreement-successor-term-start" />
				</label>
				{#if changeType === 'Renewal'}
					<label class="space-y-1">
						<span class="text-sm font-medium">New term ends</span>
						<Input type="date" bind:value={termEndOn} data-testid="agreement-successor-term-end" />
					</label>
				{/if}
			{/if}

			{#if changeType === 'Correction'}
				<label class="space-y-1">
					<span class="text-sm font-medium">Why is this correction needed?</span>
					<textarea
						class="m3-field-surface min-h-24 w-full resize-y px-3 py-2 text-sm"
						bind:value={correctionReason}
						maxlength="1000"
						placeholder="Describe the error and what the replacement should correct"
						data-testid="agreement-successor-correction-reason"
					></textarea>
					<span class="block text-xs text-muted-foreground">Required · saved with the agreement version history.</span>
				</label>
			{/if}

			<label class="space-y-1">
				<span class="text-sm font-medium">New version governs from</span>
				<Input type="date" bind:value={governingFromOn} data-testid="agreement-successor-governing-from" />
				{#if isRenewal}<span class="block text-xs text-muted-foreground">For renewals, this must match the new term start.</span>{/if}
			</label>

			{#if isRenewal}
				<section class="space-y-3" aria-labelledby="addendum-decisions-heading">
					<div>
						<h3 id="addendum-decisions-heading" class="text-sm font-semibold">Effective addendum decisions</h3>
						<p class="mt-1 text-sm text-muted-foreground">
							Choose what happens to every addendum that is effective for this relationship. These choices are saved with the new draft; the source agreement is not changed.
						</p>
					</div>

					{#if effectiveSeriesQuery.isLoading}
						<div class="flex items-center gap-2 rounded-xl border p-4 text-sm text-muted-foreground" data-testid="agreement-successor-addendum-loading">
							<Loader2 class="h-4 w-4 animate-spin" /> Loading the exact effective addendum series…
						</div>
					{:else if effectiveSeriesQuery.isError || !effectiveSeriesQuery.data}
						<div class="rounded-xl border border-destructive/30 bg-destructive/5 p-4 text-sm" data-testid="agreement-successor-addendum-error">
							<p class="font-medium">Effective addendums could not be loaded</p>
							<p class="mt-1 text-muted-foreground">Retry before creating the successor so no addendum is missed.</p>
							<Button variant="outline" size="sm" class="mt-3" onclick={() => effectiveSeriesQuery.refetch()}>Retry</Button>
						</div>
					{:else if !effectiveSeriesQuery.data.decisionRequired}
						<div class="rounded-xl border p-4 text-sm text-muted-foreground" data-testid="agreement-successor-no-addendum-decisions">
							No effective addendum series require a renewal decision as of {effectiveSeriesQuery.data.businessDate}.
						</div>
					{:else}
						<p class="text-xs text-muted-foreground">
							{effectiveSeriesQuery.data.requiredDecisionCount} decision{effectiveSeriesQuery.data.requiredDecisionCount === 1 ? '' : 's'} required as of {effectiveSeriesQuery.data.businessDate}.
						</p>
						{#each effectiveSeriesQuery.data.series as series (series.seriesPublicId)}
							<div class="space-y-3 rounded-xl border p-4" data-testid="agreement-successor-addendum-series">
								<div class="flex flex-wrap items-start justify-between gap-2">
									<div>
										<p class="font-medium">{series.title}</p>
										<p class="text-xs text-muted-foreground">
											{series.purpose} · version {series.currentVersionNumber} · effective {series.effectiveFromOn}{series.effectiveThroughOn ? ` through ${series.effectiveThroughOn}` : ' onward'}
										</p>
									</div>
									<span class="rounded-full bg-muted px-2.5 py-1 text-xs">From {series.baseAgreementNumber}</span>
								</div>

								{#if series.financialEffectCount > 0}
									<div class="space-y-1.5 rounded-lg bg-muted/30 p-3 text-xs">
										<p class="font-medium">Financial effects ({series.financialEffectCount})</p>
										{#each series.financialEffects as effect (effect.leaseAddendumFinancialEffectId)}
											<div class="flex flex-wrap justify-between gap-x-3 gap-y-1 text-muted-foreground">
												<span>{effectTypeLabel(effect.effectType)} · {effect.chargeCode}</span>
												<span>{effect.currency} {effect.amount.toFixed(2)} · {effectTiming(effect)}</span>
											</div>
											{#if effect.description}<p class="text-muted-foreground">{effect.description}</p>{/if}
										{/each}
									</div>
								{/if}

								<label class="block space-y-1.5">
									<span class="text-sm font-medium">What should happen in the new agreement?</span>
									<select
										class="m3-field-surface h-10 w-full px-3 text-sm"
										value={selectedAddendumDecisions[series.seriesPublicId] ?? ''}
										onchange={(event) => setAddendumDecision(series.seriesPublicId, event)}
										data-testid={`agreement-successor-addendum-decision-${series.seriesPublicId}`}
									>
										<option value="">Select a decision…</option>
										<option value="End">End when the new agreement begins</option>
										<option value="IncorporateIntoBase">Incorporate into the new base agreement</option>
										<option value="ReissueAsAddendum">Reissue as a new addendum draft</option>
									</select>
									<span class="block text-xs text-muted-foreground">
										{decisionConsequence(selectedAddendumDecisions[series.seriesPublicId])}
									</span>
								</label>
							</div>
						{/each}
					{/if}
				</section>
			{/if}
			{#if validationError}<p class="text-sm text-destructive" data-testid="agreement-successor-error">{validationError}</p>{/if}
		</div>

		<Dialog.Footer>
			<Button variant="outline" onclick={onclose} disabled={mutation.isPending}>Cancel</Button>
			<Button onclick={submit} disabled={mutation.isPending || (isRenewal && (effectiveSeriesQuery.isLoading || effectiveSeriesQuery.isError || !effectiveSeriesQuery.data))} class="gap-2">
				{#if mutation.isPending}<Loader2 class="h-4 w-4 animate-spin" /> Creating…{:else}<FilePlus2 class="h-4 w-4" /> Create draft{/if}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
