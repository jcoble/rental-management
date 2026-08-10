<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		leaseAddendums,
		type EditLeaseAddendumDraftRequest,
		type IssueLeaseAddendumResponse,
		type LeaseAddendumDraftDetail
	} from '$lib/api/endpoints/lease-addendums';
	import type {
		LeaseAddendumFinancialEffectType,
		LeaseAddendumPurpose,
		LeaseLegalSignerRole
	} from '$lib/api/endpoints/lease-managements';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Input } from '$lib/components/ui/input';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import SimpleSelect from '$lib/components/shared/SimpleSelect.svelte';
	import { FileSignature, Loader2, Plus, Save, Trash2 } from '@lucide/svelte';

	let {
		leaseManagementId,
		leaseAddendumId,
		onclose,
		onissued
	}: {
		leaseManagementId: number;
		leaseAddendumId: number;
		onclose: () => void;
		onissued: (result: IssueLeaseAddendumResponse) => void;
	} = $props();

	type SignerForm = {
		leaseManagementPartyId: number | null;
		tenantId: number | null;
		signerRole: LeaseLegalSignerRole;
		nameSnapshot: string;
		emailSnapshot: string;
		signingOrder: number;
		isRequired: boolean;
	};
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
	type DraftForm = {
		addendumNumber: string;
		purpose: LeaseAddendumPurpose;
		effectiveFromOn: string;
		effectiveThroughOn: string;
		signers: SignerForm[];
		financialEffects: EffectForm[];
	};

	const purposes: LeaseAddendumPurpose[] = ['Financial', 'Pet', 'Occupancy', 'Rules', 'Other'];
	const signerRoles: LeaseLegalSignerRole[] = ['PrimaryTenant', 'CoTenant', 'Guarantor', 'Manager', 'Owner', 'Other'];
	const effectTypes: LeaseAddendumFinancialEffectType[] = ['RecurringRentDelta', 'OneTimeCharge', 'DepositObligationDelta'];
	const purposeOptions = purposes.map((item) => ({
		value: item,
		label: ({ Financial: 'Rent or money change', Pet: 'Pet rules', Occupancy: 'Household change', Rules: 'Property rules', Other: 'Other change' } as const)[item]
	}));
	const signerRoleOptions = signerRoles.map((item) => ({
		value: item,
		label: ({ PrimaryTenant: 'Primary leaseholder', CoTenant: 'Co-leaseholder', Guarantor: 'Guarantor', Manager: 'Property manager', Owner: 'Property owner', Other: 'Other signer' } as const)[item]
	}));
	const effectTypeOptions = effectTypes.map((item) => ({
		value: item,
		label: ({ RecurringRentDelta: 'Ongoing rent change', OneTimeCharge: 'One-time charge', DepositObligationDelta: 'Security deposit change' } as const)[item]
	}));
	const queryClient = useQueryClient();
	let form = $state<DraftForm | null>(null);
	let seededRevision = $state(0);
	let savedFingerprint = $state('');
	let validationError = $state('');
	let issueConfirmationOpen = $state(false);
	let issueSubject = $state('');
	let editOperation: { fingerprint: string; key: string } | null = null;
	let issueOperation: { fingerprint: string; prepareKey: string; issueKey: string } | null = null;

	const draftQuery = createQuery(() => ({
		queryKey: ['lease-managements', leaseManagementId, 'addenda', leaseAddendumId, 'draft'],
		queryFn: () => leaseAddendums.getDraft(leaseManagementId, leaseAddendumId)
	}));

	function seedForm(draft: LeaseAddendumDraftDetail): DraftForm {
		return {
			addendumNumber: draft.addendumNumber,
			purpose: draft.purpose,
			effectiveFromOn: draft.effectiveFromOn,
			effectiveThroughOn: draft.effectiveThroughOn ?? '',
			signers: draft.signers.map((signer) => ({
				leaseManagementPartyId: signer.leaseManagementPartyId,
				tenantId: signer.tenantId,
				signerRole: signer.signerRole,
				nameSnapshot: signer.nameSnapshot,
				emailSnapshot: signer.emailSnapshot,
				signingOrder: signer.signingOrder,
				isRequired: true
			})),
			financialEffects: draft.financialEffects.map((effect) => ({
				effectType: effect.effectType,
				amount: String(effect.amount),
				currency: effect.currency,
				chargeCode: effect.chargeCode,
				effectiveFromOn: effect.effectiveFromOn ?? '',
				effectiveThroughOn: effect.effectiveThroughOn ?? '',
				dueOn: effect.dueOn ?? '',
				description: effect.description
			}))
		};
	}

	$effect(() => {
		const draft = draftQuery.data;
		if (!draft || draft.draftRevision === seededRevision) return;
		form = seedForm(draft);
		seededRevision = draft.draftRevision;
		savedFingerprint = draft.signers.some((signer) => !signer.isRequired)
			? ''
			: JSON.stringify(form);
		validationError = '';
		editOperation = null;
		issueOperation = null;
	});

	const isDirty = $derived(Boolean(form && JSON.stringify(form) !== savedFingerprint));

	function financialEffectError(effects: EffectForm[]) {
		for (const effect of effects) {
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

	function buildEditRequest(): EditLeaseAddendumDraftRequest | null {
		const draft = draftQuery.data;
		if (!draft || !form) return null;
		validationError = '';
		if (!form.addendumNumber.trim()) validationError = 'Addendum number is required.';
		else if (!form.effectiveFromOn) validationError = 'Effective date is required.';
		else if (form.effectiveThroughOn && form.effectiveThroughOn < form.effectiveFromOn)
			validationError = 'Effective-through date cannot be before the effective date.';
		else if (form.signers.length === 0) validationError = 'At least one signer is required.';
		else if (form.signers.some((signer) => !signer.nameSnapshot.trim() || !signer.emailSnapshot.trim()))
			validationError = 'Every signer needs a name and email snapshot.';
		else if (!form.signers.some((signer) => signer.signerRole === 'PrimaryTenant' || signer.signerRole === 'CoTenant'))
			validationError = 'At least one primary or co-tenant must be a required signer.';
		else if (new Set(form.signers.map((signer) => signer.signingOrder)).size !== form.signers.length)
			validationError = 'Signer order values must be unique.';
		else if (new Set(form.signers.map((signer) => signer.emailSnapshot.trim().toLowerCase())).size !== form.signers.length)
			validationError = 'Signer email addresses must be unique.';
		else validationError = financialEffectError(form.financialEffects);
		if (validationError) return null;
		if (!draft.documentTemplateId) {
			validationError = 'This draft has no authored template id and cannot be edited.';
			return null;
		}

		return {
			draftRevision: draft.draftRevision,
			baseAgreementId: draft.baseAgreementId,
			addendumNumber: form.addendumNumber.trim(),
			purpose: form.purpose,
			effectiveFromOn: form.effectiveFromOn,
			effectiveThroughOn: form.effectiveThroughOn || null,
			termsSchemaVersion: draft.termsSchemaVersion,
			termsPayload: draft.termsPayload,
			documentTemplateId: draft.documentTemplateId,
			signers: form.signers.map((signer) => ({
				...signer,
				isRequired: true,
				nameSnapshot: signer.nameSnapshot.trim(),
				emailSnapshot: signer.emailSnapshot.trim()
			})),
			financialEffects: form.financialEffects.map((effect) => ({
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

	function addSigner() {
		form?.signers.push({
			leaseManagementPartyId: null,
			tenantId: null,
			signerRole: 'Manager',
			nameSnapshot: '',
			emailSnapshot: '',
			signingOrder: (form?.signers.length ?? 0) + 1,
			isRequired: true
		});
	}

	function removeSigner(index: number) {
		if (!form) return;
		form.signers.splice(index, 1);
		for (let position = 0; position < form.signers.length; position += 1) {
			form.signers[position].signingOrder = position + 1;
		}
	}

	function addFinancialEffect() {
		if (!form) return;
		form.financialEffects.push({
			effectType: 'RecurringRentDelta',
			amount: '',
			currency: draftQuery.data?.baseAgreementCurrency ?? '',
			chargeCode: '',
			effectiveFromOn: form.effectiveFromOn,
			effectiveThroughOn: '',
			dueOn: '',
			description: ''
		});
	}

	function removeFinancialEffect(index: number) {
		form?.financialEffects.splice(index, 1);
	}

	function editKey(request: EditLeaseAddendumDraftRequest) {
		const fingerprint = JSON.stringify(request);
		if (!editOperation || editOperation.fingerprint !== fingerprint) {
			editOperation = { fingerprint, key: crypto.randomUUID() };
		}
		return editOperation.key;
	}

	const editMutation = createMutation(() => ({
		mutationFn: (request: EditLeaseAddendumDraftRequest) =>
			leaseAddendums.editDraft(leaseManagementId, leaseAddendumId, request, editKey(request)),
		onSuccess: async () => {
			showSuccess('Addendum draft saved.');
			await queryClient.invalidateQueries({ queryKey: ['lease-managements', leaseManagementId] });
			await draftQuery.refetch();
		},
		onError: (error) => showError(apiErrorMessage(error, 'Could not save the addendum draft.'))
	}));

	function saveDraft() {
		const request = buildEditRequest();
		if (request) editMutation.mutate(request);
	}

	function issueKeys(revision: number, subject: string) {
		const fingerprint = `${leaseAddendumId}:${revision}:${subject.trim()}`;
		if (!issueOperation || issueOperation.fingerprint !== fingerprint) {
			issueOperation = { fingerprint, prepareKey: crypto.randomUUID(), issueKey: crypto.randomUUID() };
		}
		return issueOperation;
	}

	const issueMutation = createMutation(() => ({
		mutationFn: async ({ revision, subject }: { revision: number; subject: string }) => {
			const keys = issueKeys(revision, subject);
			const prepared = await leaseAddendums.prepareIssuance(
				leaseManagementId,
				leaseAddendumId,
				revision,
				keys.prepareKey
			);
			return leaseAddendums.issue(
				leaseManagementId,
				leaseAddendumId,
				{ ...prepared, subject: subject.trim() },
				keys.issueKey
			);
		},
		onSuccess: async (result) => {
			showSuccess('Addendum issued for signature.');
			await queryClient.invalidateQueries({ queryKey: ['lease-managements', leaseManagementId] });
			onissued(result);
		},
		onError: (error) => showError(apiErrorMessage(error, 'Could not issue the addendum.'))
	}));

	function openIssueConfirmation() {
		if (!draftQuery.data || !form) return;
		if (isDirty) {
			validationError = 'Save the current draft changes before issuing.';
			return;
		}
		issueSubject = `Lease addendum ${form.addendumNumber}`;
		issueConfirmationOpen = true;
	}

	function issueAddendum() {
		const draft = draftQuery.data;
		if (draft && issueSubject.trim()) {
			issueMutation.mutate({ revision: draft.draftRevision, subject: issueSubject });
		}
	}
</script>

<Dialog.Root open onOpenChange={(open) => { if (!open && !editMutation.isPending && !issueMutation.isPending) onclose(); }}>
	<Dialog.Content class="max-h-[90vh] max-w-4xl overflow-y-auto" data-testid="addendum-draft-dialog">
		<Dialog.Header>
			<Dialog.Title>Edit addendum draft</Dialog.Title>
			<Dialog.Description>Review the lease change, dates, people who must sign, and any money changes before sending it for signature.</Dialog.Description>
		</Dialog.Header>

		{#if draftQuery.isLoading}
			<p class="text-sm text-muted-foreground">Loading exact addendum draft state…</p>
		{:else if draftQuery.isError || !draftQuery.data || !form}
			<div class="rounded-xl border border-destructive/30 bg-destructive/5 p-4 text-sm">This addendum is no longer an editable draft. Refresh addendum history.</div>
		{:else}
			{@const draft = draftQuery.data}
			<div class="space-y-5">
				<div class="flex flex-wrap gap-2 text-xs text-muted-foreground">
					<span>Series {draft.seriesPublicId}</span><span>·</span><span>Version {draft.versionNumber}</span><span>·</span><span>Revision {draft.draftRevision}</span><span>·</span><span>Base {draft.baseAgreementNumber}</span>
				</div>
				<div class="grid gap-4 md:grid-cols-4">
					<label class="space-y-1 md:col-span-2"><span class="text-sm font-medium">Addendum number</span><Input bind:value={form.addendumNumber} data-testid="addendum-draft-number" /></label>
					<label class="space-y-1"><span class="text-sm font-medium">What is changing?</span><SimpleSelect bind:value={form.purpose} options={purposeOptions} /></label>
					<label class="space-y-1"><span class="text-sm font-medium">Change begins</span><DatePicker bind:value={form.effectiveFromOn} testid="addendum-draft-effective-from" /></label>
					<label class="space-y-1"><span class="text-sm font-medium">Change ends (optional)</span><DatePicker bind:value={form.effectiveThroughOn} /></label>
				</div>

				<div class="space-y-3">
					<div class="flex items-center justify-between gap-3"><div><h3 class="font-medium">People who must sign</h3><p class="text-xs text-muted-foreground">Review these names and email addresses. They cannot be changed after the addendum is sent.</p></div><Button variant="outline" size="sm" class="gap-2" onclick={addSigner}><Plus class="h-4 w-4" /> Add signer</Button></div>
					{#each form.signers as signer, index}
						<div class="grid gap-3 rounded-xl bg-muted/30 p-3 md:grid-cols-[5rem_1fr_1fr_10rem_auto_auto]">
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Order</span><Input type="number" min="1" bind:value={signer.signingOrder} /></label>
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Name</span><Input bind:value={signer.nameSnapshot} /></label>
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Email</span><Input type="email" bind:value={signer.emailSnapshot} /></label>
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Role</span><SimpleSelect bind:value={signer.signerRole} options={signerRoleOptions} /></label>
							<p class="pt-6 text-sm text-muted-foreground">Required signer</p>
							<Button variant="ghost" size="icon" class="mt-5" aria-label="Remove signer" onclick={() => removeSigner(index)}><Trash2 class="h-4 w-4" /></Button>
						</div>
					{/each}
				</div>

				<div class="space-y-3">
					<div class="flex items-center justify-between gap-3"><div><h3 class="font-medium">Rent, charge, or deposit changes</h3><p class="text-xs text-muted-foreground">Add only the money changes created by this addendum.</p></div><Button variant="outline" size="sm" class="gap-2" onclick={addFinancialEffect}><Plus class="h-4 w-4" /> Add money change</Button></div>
					{#if form.financialEffects.length === 0}<p class="rounded-xl bg-muted/30 p-3 text-sm text-muted-foreground">This addendum does not change rent, charges, or the security deposit.</p>{/if}
					{#each form.financialEffects as effect, index}
						<div class="grid gap-3 rounded-xl bg-muted/30 p-3 md:grid-cols-3">
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Money change</span><SimpleSelect bind:value={effect.effectType} options={effectTypeOptions} /></label>
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Amount</span><Input type="number" step="0.01" bind:value={effect.amount} /></label>
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Currency</span><Input maxlength={3} bind:value={effect.currency} /></label>
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Charge code</span><Input bind:value={effect.chargeCode} /></label>
							{#if effect.effectType === 'OneTimeCharge'}
								<label class="space-y-1"><span class="text-xs text-muted-foreground">Due date</span><DatePicker bind:value={effect.dueOn} /></label>
							{:else}
								<label class="space-y-1"><span class="text-xs text-muted-foreground">Begins</span><DatePicker bind:value={effect.effectiveFromOn} /></label>
								<label class="space-y-1"><span class="text-xs text-muted-foreground">Ends (optional)</span><DatePicker bind:value={effect.effectiveThroughOn} /></label>
							{/if}
							<label class="space-y-1 md:col-span-3"><span class="text-xs text-muted-foreground">Description</span><Input bind:value={effect.description} /></label>
							<Button variant="ghost" size="sm" class="justify-self-end gap-2 md:col-span-3" onclick={() => removeFinancialEffect(index)}><Trash2 class="h-4 w-4" /> Remove effect</Button>
						</div>
					{/each}
				</div>

				{#if validationError}<p class="text-sm text-destructive" data-testid="addendum-draft-error">{validationError}</p>{/if}
				{#if issueConfirmationOpen}
					<div class="space-y-3 rounded-xl border border-primary/30 bg-primary/5 p-4" data-testid="addendum-issue-confirmation">
						<div><p class="font-medium">Issue this exact revision for signature?</p><p class="text-sm text-muted-foreground">The server renders and fingerprints the PDF, then freezes this addendum and its child snapshots.</p></div>
						<label class="space-y-1"><span class="text-sm font-medium">Signature request subject</span><Input bind:value={issueSubject} /></label>
						<div class="flex justify-end gap-2"><Button variant="outline" onclick={() => (issueConfirmationOpen = false)} disabled={issueMutation.isPending}>Not yet</Button><Button onclick={issueAddendum} disabled={!issueSubject.trim() || issueMutation.isPending} class="gap-2">{#if issueMutation.isPending}<Loader2 class="h-4 w-4 animate-spin" /> Preparing &amp; issuing…{:else}<FileSignature class="h-4 w-4" /> Issue for signature{/if}</Button></div>
					</div>
				{/if}
			</div>
		{/if}

		<Dialog.Footer>
			<Button variant="outline" onclick={onclose} disabled={editMutation.isPending || issueMutation.isPending}>Close</Button>
			{#if draftQuery.data && form}
				<Button variant="outline" class="gap-2" onclick={openIssueConfirmation} disabled={isDirty || editMutation.isPending || issueMutation.isPending}><FileSignature class="h-4 w-4" /> Prepare &amp; issue</Button>
				<Button class="gap-2" onclick={saveDraft} disabled={!isDirty || editMutation.isPending || issueMutation.isPending || !draftQuery.data.documentTemplateId}>{#if editMutation.isPending}<Loader2 class="h-4 w-4 animate-spin" /> Saving…{:else}<Save class="h-4 w-4" /> Save draft{/if}</Button>
			{/if}
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
