<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		leaseAddendums,
		type CreateLeaseAddendumDraftRequest,
		type LeaseAddendumDraftMutationResponse,
		type LeaseAddendumSignerRequest
	} from '$lib/api/endpoints/lease-addendums';
	import {
		leaseManagements,
		type LeaseAddendumFinancialEffectType,
		type LeaseAddendumPurpose,
		type LeaseLegalSignerRole
	} from '$lib/api/endpoints/lease-managements';
	import { documentTemplates } from '$lib/api/endpoints/document-templates';
	import type { LeaseAgreementSummary } from '$lib/types';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Input } from '$lib/components/ui/input';
	import { FilePlus2, Loader2, Plus, Trash2 } from '@lucide/svelte';

	let {
		leaseManagementId,
		propertyId,
		baseAgreement,
		onclose,
		oncreated
	}: {
		leaseManagementId: number;
		propertyId: number;
		baseAgreement: LeaseAgreementSummary;
		onclose: () => void;
		oncreated: (result: LeaseAddendumDraftMutationResponse) => void;
	} = $props();

	type SignerForm = LeaseAddendumSignerRequest;
	type EffectForm = {
		effectType: LeaseAddendumFinancialEffectType;
		amount: string;
		currency: string;
		chargeCode: string;
		effectiveFromOn: string;
		effectiveThroughOn: string;
		dueOn: string;
		description: string;
	};

	const purposes: LeaseAddendumPurpose[] = ['Financial', 'Pet', 'Occupancy', 'Rules', 'Other'];
	const signerRoles: LeaseLegalSignerRole[] = ['PrimaryTenant', 'CoTenant', 'Guarantor', 'Manager', 'Owner', 'Other'];
	const effectTypes: LeaseAddendumFinancialEffectType[] = ['RecurringRentDelta', 'OneTimeCharge', 'DepositObligationDelta'];
	const queryClient = useQueryClient();
	let addendumNumber = $state('');
	let purpose = $state<LeaseAddendumPurpose>('Other');
	let effectiveFromOn = $state('');
	let effectiveThroughOn = $state('');
	let documentTemplateId = $state('');

	function signerRole(role: string): LeaseLegalSignerRole {
		switch (role) {
			case 'PrimaryTenant': return 'PrimaryTenant';
			case 'CoTenant': return 'CoTenant';
			case 'Guarantor': return 'Guarantor';
			default: return 'Other';
		}
	}

	let signers = $state<SignerForm[]>([]);
	let currentPartiesSeeded = $state(false);
	let financialEffects = $state<EffectForm[]>([]);
	let validationError = $state('');
	let operation: { fingerprint: string; key: string } | null = null;

	const templatesQuery = createQuery(() => ({
		queryKey: ['document-templates', 'addendum-create', propertyId],
		queryFn: () => documentTemplates.listPage({
			kind: 'Lease',
			status: 'Active',
			propertyId,
			take: 200,
			sort: 'name'
		})
	}));
	const currentPartiesQuery = createQuery(() => ({
		queryKey: ['lease-managements', leaseManagementId, 'current-parties-context'],
		queryFn: () => leaseManagements.getCurrentPartiesContext(leaseManagementId)
	}));

	$effect(() => {
		const context = currentPartiesQuery.data;
		if (!context || currentPartiesSeeded) return;
		signers = context.parties
			.filter((party) => party.role === 'PrimaryTenant' || party.role === 'CoTenant')
			.map((party, index) => ({
			leaseManagementPartyId: party.leaseManagementPartyId,
			tenantId: party.tenantId,
			signerRole: signerRole(party.role),
			nameSnapshot: party.tenantName,
			emailSnapshot: party.email ?? '',
			signingOrder: index + 1,
			isRequired: true
		}));
		currentPartiesSeeded = true;
	});

	function addSigner() {
		signers.push({
			leaseManagementPartyId: null,
			tenantId: null,
			signerRole: 'Manager',
			nameSnapshot: '',
			emailSnapshot: '',
			signingOrder: signers.length + 1,
			isRequired: true
		});
	}

	function removeSigner(index: number) {
		signers.splice(index, 1);
		for (let position = 0; position < signers.length; position += 1) {
			signers[position].signingOrder = position + 1;
		}
	}

	function addFinancialEffect() {
		financialEffects.push({
			effectType: 'RecurringRentDelta',
			amount: '',
			currency: '',
			chargeCode: '',
			effectiveFromOn: effectiveFromOn,
			effectiveThroughOn: '',
			dueOn: '',
			description: ''
		});
	}

	function removeFinancialEffect(index: number) {
		financialEffects.splice(index, 1);
	}

	function financialEffectError() {
		for (const effect of financialEffects) {
			const amount = Number(effect.amount);
			if (!Number.isFinite(amount) || !effect.currency.trim() || effect.currency.trim().length !== 3 || !effect.chargeCode.trim() || !effect.description.trim())
				return 'Every financial effect needs an amount, three-letter currency, charge code, and description.';
			if (effect.effectType === 'OneTimeCharge' && (amount <= 0 || !effect.dueOn))
				return 'A one-time charge needs a positive amount and due date.';
			if (effect.effectType !== 'OneTimeCharge' && (amount === 0 || !effect.effectiveFromOn))
				return 'A recurring rent or deposit change needs a nonzero amount and effective date.';
			if (effect.effectiveThroughOn && effect.effectiveFromOn && effect.effectiveThroughOn < effect.effectiveFromOn)
				return 'A financial effect cannot end before it begins.';
		}
		return '';
	}

	function buildRequest(): CreateLeaseAddendumDraftRequest | null {
		validationError = '';
		if (!addendumNumber.trim()) validationError = 'Addendum number is required.';
		else if (!effectiveFromOn) validationError = 'Effective date is required.';
		else if (effectiveThroughOn && effectiveThroughOn < effectiveFromOn)
			validationError = 'Effective-through date cannot be before the effective date.';
		else if (!documentTemplateId) validationError = 'Choose an active lease template.';
		else if (signers.length === 0) validationError = 'At least one signer is required.';
		else if (signers.some((signer) => !signer.nameSnapshot.trim() || !signer.emailSnapshot.trim()))
			validationError = 'Every signer needs a name and email snapshot.';
		else if (!signers.some((signer) => signer.signerRole === 'PrimaryTenant' || signer.signerRole === 'CoTenant'))
			validationError = 'At least one primary or co-tenant must be a required signer.';
		else if (new Set(signers.map((signer) => signer.signingOrder)).size !== signers.length)
			validationError = 'Signer order values must be unique.';
		else if (new Set(signers.map((signer) => signer.emailSnapshot.trim().toLowerCase())).size !== signers.length)
			validationError = 'Signer email addresses must be unique.';
		else validationError = financialEffectError();
		if (validationError) return null;

		return {
			baseAgreementId: baseAgreement.leaseAgreementId,
			addendumNumber: addendumNumber.trim(),
			purpose,
			effectiveFromOn,
			effectiveThroughOn: effectiveThroughOn || null,
			termsSchemaVersion: 1,
			termsPayload: {},
			documentTemplateId: Number(documentTemplateId),
			signers: signers.map((signer) => ({
				...signer,
				isRequired: true,
				nameSnapshot: signer.nameSnapshot.trim(),
				emailSnapshot: signer.emailSnapshot.trim()
			})),
			financialEffects: financialEffects.map((effect) => ({
				effectType: effect.effectType,
				amount: Number(effect.amount),
				currency: effect.currency.trim().toUpperCase(),
				chargeCode: effect.chargeCode.trim(),
				effectiveFromOn: effect.effectType === 'OneTimeCharge' ? null : effect.effectiveFromOn || null,
				effectiveThroughOn: effect.effectType === 'OneTimeCharge' ? null : effect.effectiveThroughOn || null,
				dueOn: effect.effectType === 'OneTimeCharge' ? effect.dueOn || null : null,
				description: effect.description.trim()
			}))
		};
	}

	function operationKey(request: CreateLeaseAddendumDraftRequest) {
		const fingerprint = JSON.stringify(request);
		if (!operation || operation.fingerprint !== fingerprint) {
			operation = { fingerprint, key: crypto.randomUUID() };
		}
		return operation.key;
	}

	const mutation = createMutation(() => ({
		mutationFn: (request: CreateLeaseAddendumDraftRequest) =>
			leaseAddendums.createDraft(leaseManagementId, request, operationKey(request)),
		onSuccess: async (result) => {
			showSuccess('Addendum draft created.');
			await queryClient.invalidateQueries({ queryKey: ['lease-managements', leaseManagementId] });
			oncreated(result);
		},
		onError: (error) => showError(apiErrorMessage(error, 'Could not create the addendum draft.'))
	}));

	function submit() {
		const request = buildRequest();
		if (request) mutation.mutate(request);
	}
</script>

<Dialog.Root open onOpenChange={(open) => { if (!open && !mutation.isPending) onclose(); }}>
	<Dialog.Content class="max-h-[90vh] max-w-4xl overflow-y-auto" data-testid="addendum-create-dialog">
		<Dialog.Header>
			<Dialog.Title>Create addendum draft</Dialog.Title>
			<Dialog.Description>Creates a new editable addendum against executed agreement {baseAgreement.agreementNumber}. Nothing changes legally until the draft is issued and fully signed.</Dialog.Description>
		</Dialog.Header>

		<div class="space-y-5">
			<div class="grid gap-4 md:grid-cols-4">
				<label class="space-y-1 md:col-span-2"><span class="text-sm font-medium">Addendum number</span><Input bind:value={addendumNumber} data-testid="addendum-create-number" /></label>
				<label class="space-y-1"><span class="text-sm font-medium">Purpose</span><select bind:value={purpose} class="m3-field-surface h-10 w-full px-3 text-sm">{#each purposes as item}<option value={item}>{item}</option>{/each}</select></label>
				<label class="space-y-1"><span class="text-sm font-medium">Active template</span><select bind:value={documentTemplateId} class="m3-field-surface h-10 w-full px-3 text-sm" disabled={templatesQuery.isLoading || templatesQuery.isError}><option value="">Select template…</option>{#each templatesQuery.data?.items ?? [] as template (template.id)}<option value={String(template.id)}>{template.name} · v{template.version}</option>{/each}</select></label>
				<label class="space-y-1"><span class="text-sm font-medium">Effective from</span><Input type="date" bind:value={effectiveFromOn} /></label>
				<label class="space-y-1"><span class="text-sm font-medium">Effective through</span><Input type="date" bind:value={effectiveThroughOn} /></label>
			</div>
			{#if templatesQuery.isError}<p class="text-sm text-destructive">Active templates could not be loaded. Retry this dialog before creating a draft.</p>{/if}
			{#if currentPartiesQuery.isError}<p class="text-sm text-destructive">Current relationship parties could not be loaded. Retry this dialog before creating a draft.</p>{/if}

			<div class="space-y-3">
				<div class="flex items-center justify-between gap-3"><div><h3 class="font-medium">Signer snapshots</h3><p class="text-xs text-muted-foreground">Current primary and co-tenants are prefilled. Non-signer household members stay outside the legal packet.</p></div><Button variant="outline" size="sm" class="gap-2" onclick={addSigner}><Plus class="h-4 w-4" /> Add signer</Button></div>
				{#each signers as signer, index}
					<div class="grid gap-3 rounded-xl border p-3 md:grid-cols-[5rem_1fr_1fr_10rem_auto_auto]">
						<label class="space-y-1"><span class="text-xs text-muted-foreground">Order</span><Input type="number" min="1" bind:value={signer.signingOrder} /></label>
						<label class="space-y-1"><span class="text-xs text-muted-foreground">Name</span><Input bind:value={signer.nameSnapshot} /></label>
						<label class="space-y-1"><span class="text-xs text-muted-foreground">Email</span><Input type="email" bind:value={signer.emailSnapshot} /></label>
						<label class="space-y-1"><span class="text-xs text-muted-foreground">Role</span><select bind:value={signer.signerRole} class="m3-field-surface h-10 w-full px-2 text-sm">{#each signerRoles as role}<option value={role}>{role}</option>{/each}</select></label>
						<p class="pt-6 text-sm text-muted-foreground">Required signer</p>
						<Button variant="ghost" size="icon" class="mt-5" aria-label="Remove signer" onclick={() => removeSigner(index)}><Trash2 class="h-4 w-4" /></Button>
					</div>
				{/each}
			</div>

			<div class="space-y-3">
				<div class="flex items-center justify-between gap-3"><div><h3 class="font-medium">Structured financial effects</h3><p class="text-xs text-muted-foreground">Optional. Currency must match the base agreement; billing reads these effects from the database.</p></div><Button variant="outline" size="sm" class="gap-2" onclick={addFinancialEffect}><Plus class="h-4 w-4" /> Add effect</Button></div>
				{#if financialEffects.length === 0}<p class="rounded-xl border p-3 text-sm text-muted-foreground">No financial effects. This is valid for nonfinancial addendums.</p>{/if}
				{#each financialEffects as effect, index}
					<div class="grid gap-3 rounded-xl border p-3 md:grid-cols-3">
						<label class="space-y-1"><span class="text-xs text-muted-foreground">Type</span><select bind:value={effect.effectType} class="m3-field-surface h-10 w-full px-2 text-sm">{#each effectTypes as item}<option value={item}>{item}</option>{/each}</select></label>
						<label class="space-y-1"><span class="text-xs text-muted-foreground">Amount</span><Input type="number" step="0.01" bind:value={effect.amount} /></label>
						<label class="space-y-1"><span class="text-xs text-muted-foreground">Currency</span><Input maxlength={3} placeholder="USD" bind:value={effect.currency} /></label>
						<label class="space-y-1"><span class="text-xs text-muted-foreground">Charge code</span><Input bind:value={effect.chargeCode} /></label>
						{#if effect.effectType === 'OneTimeCharge'}<label class="space-y-1"><span class="text-xs text-muted-foreground">Due on</span><Input type="date" bind:value={effect.dueOn} /></label>{:else}<label class="space-y-1"><span class="text-xs text-muted-foreground">Effective from</span><Input type="date" bind:value={effect.effectiveFromOn} /></label><label class="space-y-1"><span class="text-xs text-muted-foreground">Effective through</span><Input type="date" bind:value={effect.effectiveThroughOn} /></label>{/if}
						<label class="space-y-1 md:col-span-2"><span class="text-xs text-muted-foreground">Description</span><Input bind:value={effect.description} /></label>
						<Button variant="ghost" size="sm" class="mt-5 justify-self-end gap-2" onclick={() => removeFinancialEffect(index)}><Trash2 class="h-4 w-4" /> Remove</Button>
					</div>
				{/each}
			</div>

			{#if validationError}<p class="text-sm text-destructive" data-testid="addendum-create-error">{validationError}</p>{/if}
		</div>

		<Dialog.Footer>
			<Button variant="outline" onclick={onclose} disabled={mutation.isPending}>Cancel</Button>
			<Button class="gap-2" onclick={submit} disabled={mutation.isPending || templatesQuery.isLoading || templatesQuery.isError || currentPartiesQuery.isLoading || currentPartiesQuery.isError}>{#if mutation.isPending}<Loader2 class="h-4 w-4 animate-spin" /> Creating…{:else}<FilePlus2 class="h-4 w-4" /> Create draft{/if}</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
