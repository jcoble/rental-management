<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import {
		leaseManagements,
		type CreateLeaseAgreementSuccessorDraftRequest,
		type LeaseAgreementDraftMutationResponse,
		type LeaseAgreementRenewalFinancialEffectSummary,
		type LeaseRenewalAddendumDecisionType
	} from '$lib/api/endpoints/lease-managements';
	import { documentTemplates } from '$lib/api/endpoints/document-templates';
	import type { LeaseAgreementSummary } from '$lib/types';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { leaseAgreementChangeTypeLabel } from '$lib/leases/lease-list-labels';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import SimpleSelect from '$lib/components/shared/SimpleSelect.svelte';
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
	let documentTemplateId = $state('');
	let selectedAddendumDecisions = $state<Record<string, LeaseRenewalAddendumDecisionType | undefined>>({});
	let operation: { fingerprint: string; key: string } | null = null;

	const sourceRequiresTemplate = $derived(source.hasSourceScan);
	const title = $derived(`Change the lease: ${leaseAgreementChangeTypeLabel(changeType)}`);
	const relationshipQuery = createQuery(() => ({
		queryKey: ['lease-managements', leaseManagementId, 'successor-template-context'],
		queryFn: () => leaseManagements.get(leaseManagementId),
		enabled: sourceRequiresTemplate
	}));
	const templatePropertyId = $derived(relationshipQuery.data?.summary.propertyId ?? null);
	const templatesQuery = createQuery(() => ({
		queryKey: ['document-templates', 'agreement-successor', templatePropertyId],
		queryFn: () => documentTemplates.listPage({
			kind: 'Lease',
			status: 'Active',
			propertyId: templatePropertyId!,
			take: 200,
			sort: 'name'
		}),
		enabled: sourceRequiresTemplate && templatePropertyId != null
	}));
	const templateOptions = $derived((templatesQuery.data?.items ?? []).map((template) => ({
		value: String(template.id),
		label: `${template.name} · version ${template.version}${template.defaultForPortfolio ? ' · default' : ''}`
	})));
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

	$effect(() => {
		if (!sourceRequiresTemplate) {
			documentTemplateId = '';
			return;
		}
		const templates = templatesQuery.data?.items ?? [];
		if (templates.length === 0 || templates.some((template) => String(template.id) === documentTemplateId)) return;
		const defaultTemplates = templates.filter((template) => template.defaultForPortfolio);
		const defaultTemplate = defaultTemplates.length === 1
			? defaultTemplates[0]
			: templates.length === 1
				? templates[0]
				: null;
		if (defaultTemplate) documentTemplateId = String(defaultTemplate.id);
	});

	function setAddendumDecision(seriesPublicId: string, value: string | undefined) {
		selectedAddendumDecisions = {
			...selectedAddendumDecisions,
			[seriesPublicId]: value as LeaseRenewalAddendumDecisionType | undefined
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
		let selectedDocumentTemplateId: number | null = null;
		if (sourceRequiresTemplate) {
			if (relationshipQuery.isLoading || templatesQuery.isLoading) {
				validationError = 'Wait for active lease templates to load before creating the draft.';
				return null;
			}
			if (relationshipQuery.isError || templatesQuery.isError || !templatesQuery.data) {
				validationError = 'Could not load active lease templates for this property. Retry before creating the draft.';
				return null;
			}
			if (templatesQuery.data.items.length === 0) {
				validationError = 'Create or activate a lease template for this property before drafting a successor.';
				return null;
			}
			selectedDocumentTemplateId = Number(documentTemplateId);
			if (!Number.isInteger(selectedDocumentTemplateId) || selectedDocumentTemplateId <= 0) {
				validationError = 'Choose an active lease template before creating the draft.';
				return null;
			}
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
			documentTemplateId: selectedDocumentTemplateId,
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
			showSuccess(`${leaseAgreementChangeTypeLabel(changeType)} draft created.`);
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
			<Dialog.Description>Create the next editable lease version. The current signed lease stays in effect until the replacement is fully signed.</Dialog.Description>
		</Dialog.Header>

		<div class="space-y-4">
			<div class="rounded-xl bg-muted/40 p-4 text-sm">
				<p class="font-medium">From {source.agreementNumber} · version {source.versionNumber}</p>
				<p class="text-muted-foreground">{source.termStartOn} to {source.termEndOn ?? 'month-to-month'}</p>
			</div>

			{#if isReplacement}
				<div class="rounded-xl bg-muted/40 p-4 text-sm text-muted-foreground">
					The current start and end dates will be copied. After creating the draft, review the corrected terms and signers.
				</div>
			{:else}
				<label class="space-y-1">
					<span class="text-sm font-medium">New term starts</span>
					<DatePicker bind:value={termStartOn} testid="agreement-successor-term-start" />
				</label>
				{#if changeType === 'Renewal'}
					<label class="space-y-1">
						<span class="text-sm font-medium">New term ends</span>
						<DatePicker bind:value={termEndOn} testid="agreement-successor-term-end" />
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
				<DatePicker bind:value={governingFromOn} testid="agreement-successor-governing-from" />
				{#if isRenewal}<span class="block text-xs text-muted-foreground">For renewals, this must match the new term start.</span>{/if}
			</label>

			{#if sourceRequiresTemplate}
				<section class="space-y-2 rounded-xl border p-4" aria-labelledby="agreement-successor-template-heading">
					<div>
						<h3 id="agreement-successor-template-heading" class="text-sm font-semibold">Lease template</h3>
						<p class="mt-1 text-sm text-muted-foreground">
							Choose the active template that turns this imported agreement into an editable successor draft.
						</p>
					</div>
					{#if relationshipQuery.isLoading || templatesQuery.isLoading}
						<div class="flex items-center gap-2 text-sm text-muted-foreground" data-testid="agreement-successor-template-loading">
							<Loader2 class="h-4 w-4 animate-spin" /> Loading active lease templates…
						</div>
					{:else if relationshipQuery.isError || templatesQuery.isError}
						<div class="space-y-2 text-sm" data-testid="agreement-successor-template-error">
							<p class="font-medium text-destructive">Active lease templates could not be loaded.</p>
							<Button variant="outline" size="sm" onclick={() => { relationshipQuery.refetch(); templatesQuery.refetch(); }}>Retry</Button>
						</div>
					{:else if templatesQuery.data?.items.length === 0}
						<p class="text-sm text-destructive" data-testid="agreement-successor-template-empty">
							No active lease template is available for this property.
						</p>
					{:else}
						<label class="block space-y-1.5">
							<span class="text-sm font-medium">Template for successor draft</span>
							<SimpleSelect
								bind:value={documentTemplateId}
								options={templateOptions}
								placeholder="Choose a template"
								disabled={mutation.isPending}
								testid="agreement-successor-document-template"
							/>
						</label>
					{/if}
				</section>
			{/if}

			{#if isRenewal}
				<section class="space-y-3" aria-labelledby="addendum-decisions-heading">
					<div>
						<h3 id="addendum-decisions-heading" class="text-sm font-semibold">What should happen to existing lease changes?</h3>
						<p class="mt-1 text-sm text-muted-foreground">
							Choose whether each current addendum ends, becomes part of the new lease, or is copied into a new addendum draft.
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
									<Select.Root
										type="single"
										value={selectedAddendumDecisions[series.seriesPublicId] ?? ''}
										onValueChange={(value) => setAddendumDecision(series.seriesPublicId, value)}
									>
										<Select.Trigger class="w-full" data-testid={`agreement-successor-addendum-decision-${series.seriesPublicId}`}>
											{selectedAddendumDecisions[series.seriesPublicId] === 'End'
												? 'End when the new lease begins'
												: selectedAddendumDecisions[series.seriesPublicId] === 'IncorporateIntoBase'
													? 'Include in the new lease'
													: selectedAddendumDecisions[series.seriesPublicId] === 'ReissueAsAddendum'
														? 'Copy into a new addendum'
														: 'Choose what happens'}
										</Select.Trigger>
										<Select.Content>
											<Select.Item value="End" label="End when the new lease begins">End when the new lease begins</Select.Item>
											<Select.Item value="IncorporateIntoBase" label="Include in the new lease">Include in the new lease</Select.Item>
											<Select.Item value="ReissueAsAddendum" label="Copy into a new addendum">Copy into a new addendum</Select.Item>
										</Select.Content>
									</Select.Root>
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
