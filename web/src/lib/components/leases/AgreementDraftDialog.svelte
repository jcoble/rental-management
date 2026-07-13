<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		leaseManagements,
		type EditLeaseAgreementDraftRequest,
		type IssueLeaseAgreementResponse,
		type LeaseAgreementDraftDetail,
		type LeaseLegalSignerRole
	} from '$lib/api/endpoints/lease-managements';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Input } from '$lib/components/ui/input';
	import { FileSignature, Loader2, Save } from '@lucide/svelte';

	let {
		leaseManagementId,
		leaseAgreementId,
		onclose,
		onissued
	}: {
		leaseManagementId: number;
		leaseAgreementId: number;
		onclose: () => void;
		onissued: (result: IssueLeaseAgreementResponse) => void;
	} = $props();

	type DraftForm = {
		agreementNumber: string;
		termType: 'FixedTerm' | 'MonthToMonth';
		termStartOn: string;
		termEndOn: string;
		governingFromOn: string;
		baseRentAmount: string;
		rentDueDay: string;
		securityDepositObligation: string;
		lateFeeAmount: string;
		gracePeriodDays: string;
		signers: Array<{
			leaseManagementPartyId: number | null;
			tenantId: number | null;
			signerRole: LeaseLegalSignerRole;
			nameSnapshot: string;
			emailSnapshot: string;
			signingOrder: number;
			isRequired: boolean;
		}>;
	};

	const signerRoles: LeaseLegalSignerRole[] = [
		'PrimaryTenant',
		'CoTenant',
		'Guarantor',
		'Manager',
		'Owner',
		'Other'
	];
	const queryClient = useQueryClient();
	let form = $state<DraftForm | null>(null);
	let seededRevision = $state(0);
	let savedFingerprint = $state('');
	let validationError = $state('');
	let issueConfirmationOpen = $state(false);
	let issueSubject = $state('');
	let editOperation: { fingerprint: string; key: string } | null = null;
	let issueOperation:
		| { fingerprint: string; prepareKey: string; issueKey: string }
		| null = null;

	const draftQuery = createQuery(() => ({
		queryKey: ['lease-managements', leaseManagementId, 'agreements', leaseAgreementId, 'draft'],
		queryFn: () => leaseManagements.getAgreementDraft(leaseManagementId, leaseAgreementId)
	}));

	function seedForm(draft: LeaseAgreementDraftDetail): DraftForm {
		return {
			agreementNumber: draft.agreementNumber,
			termType: draft.termType,
			termStartOn: draft.termStartOn,
			termEndOn: draft.termEndOn ?? '',
			governingFromOn: draft.governingFromOn,
			baseRentAmount: String(draft.baseRentAmount),
			rentDueDay: String(draft.rentDueDay),
			securityDepositObligation: String(draft.securityDepositObligation),
			lateFeeAmount: String(draft.lateFeeAmount),
			gracePeriodDays: String(draft.gracePeriodDays),
			signers: draft.signers.map((signer) => ({
				leaseManagementPartyId: signer.leaseManagementPartyId,
				tenantId: signer.tenantId,
				signerRole: signer.signerRole,
				nameSnapshot: signer.nameSnapshot,
				emailSnapshot: signer.emailSnapshot,
				signingOrder: signer.signingOrder,
				isRequired: true
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

	function buildEditRequest(): EditLeaseAgreementDraftRequest | null {
		const draft = draftQuery.data;
		if (!draft || !form) return null;
		validationError = '';
		const baseRentAmount = Number(form.baseRentAmount);
		const rentDueDay = Number(form.rentDueDay);
		const securityDepositObligation = Number(form.securityDepositObligation);
		const lateFeeAmount = Number(form.lateFeeAmount);
		const gracePeriodDays = Number(form.gracePeriodDays);
		if (!form.agreementNumber.trim()) validationError = 'Agreement number is required.';
		else if (!form.termStartOn || !form.governingFromOn)
			validationError = 'Term start and governing dates are required.';
		else if (form.termType === 'FixedTerm' && !form.termEndOn)
			validationError = 'A fixed-term agreement needs an end date.';
		else if (form.termEndOn && form.termEndOn < form.termStartOn)
			validationError = 'Term end cannot be before term start.';
		else if (
			form.governingFromOn < form.termStartOn ||
			(form.termEndOn && form.governingFromOn > form.termEndOn)
		)
			validationError = 'The governing date must fall within the agreement term.';
		else if (!Number.isFinite(baseRentAmount) || baseRentAmount < 0)
			validationError = 'Base rent must be zero or greater.';
		else if (!Number.isInteger(rentDueDay) || rentDueDay < 1 || rentDueDay > 31)
			validationError = 'Rent due day must be from 1 to 31.';
		else if (!Number.isFinite(securityDepositObligation) || securityDepositObligation < 0)
			validationError = 'Security deposit must be zero or greater.';
		else if (!Number.isFinite(lateFeeAmount) || lateFeeAmount < 0)
			validationError = 'Late fee must be zero or greater.';
		else if (!Number.isInteger(gracePeriodDays) || gracePeriodDays < 0)
			validationError = 'Grace period must be a whole number of days.';
		else if (form.signers.length === 0) validationError = 'At least one signer is required.';
		else if (
			form.signers.some(
				(signer) => !signer.nameSnapshot.trim() || !signer.emailSnapshot.trim()
			)
		)
			validationError = 'Every signer needs a name and email snapshot.';
		else if (new Set(form.signers.map((signer) => signer.signingOrder)).size !== form.signers.length)
			validationError = 'Signer order values must be unique.';
		if (validationError) return null;
		if (!draft.documentTemplateId) {
			validationError = 'This draft has no authored template id and cannot be edited in this flow.';
			return null;
		}

		return {
			draftRevision: draft.draftRevision,
			agreementNumber: form.agreementNumber.trim(),
			termType: form.termType,
			termStartOn: form.termStartOn,
			termEndOn: form.termType === 'MonthToMonth' ? null : form.termEndOn,
			governingFromOn: form.governingFromOn,
			baseRentAmount,
			rentDueDay,
			securityDepositObligation,
			lateFeeAmount,
			gracePeriodDays,
			termsSchemaVersion: draft.termsSchemaVersion,
			termsPayload: draft.termsPayload,
			documentTemplateId: draft.documentTemplateId,
				signers: form.signers.map((signer) => ({ ...signer, isRequired: true }))
		};
	}

	function editKey(request: EditLeaseAgreementDraftRequest) {
		const fingerprint = JSON.stringify(request);
		if (!editOperation || editOperation.fingerprint !== fingerprint) {
			editOperation = { fingerprint, key: crypto.randomUUID() };
		}
		return editOperation.key;
	}

	const editMutation = createMutation(() => ({
		mutationFn: (request: EditLeaseAgreementDraftRequest) =>
			leaseManagements.editAgreementDraft(
				leaseManagementId,
				leaseAgreementId,
				request,
				editKey(request)
			),
		onSuccess: async () => {
			showSuccess('Agreement draft saved.');
			await queryClient.invalidateQueries({
				queryKey: ['lease-managements', leaseManagementId]
			});
			await draftQuery.refetch();
		},
		onError: (error) => showError(apiErrorMessage(error, 'Could not save the agreement draft.'))
	}));

	function saveDraft() {
		const request = buildEditRequest();
		if (request) editMutation.mutate(request);
	}

	function issueKeys(revision: number, subject: string) {
		const fingerprint = `${leaseAgreementId}:${revision}:${subject.trim()}`;
		if (!issueOperation || issueOperation.fingerprint !== fingerprint) {
			issueOperation = {
				fingerprint,
				prepareKey: crypto.randomUUID(),
				issueKey: crypto.randomUUID()
			};
		}
		return issueOperation;
	}

	const issueMutation = createMutation(() => ({
		mutationFn: async ({ revision, subject }: { revision: number; subject: string }) => {
			const keys = issueKeys(revision, subject);
			const prepared = await leaseManagements.prepareAgreementIssuance(
				leaseManagementId,
				leaseAgreementId,
				revision,
				keys.prepareKey
			);
			return leaseManagements.issueAgreement(
				leaseManagementId,
				leaseAgreementId,
				{ ...prepared, subject: subject.trim() },
				keys.issueKey
			);
		},
		onSuccess: async (result) => {
			showSuccess('Agreement issued for signature.');
			await queryClient.invalidateQueries({ queryKey: ['lease-managements', leaseManagementId] });
			onissued(result);
		},
		onError: (error) => showError(apiErrorMessage(error, 'Could not issue the agreement.'))
	}));

	function openIssueConfirmation() {
		if (!draftQuery.data || !form) return;
		if (isDirty) {
			validationError = 'Save the current draft changes before issuing.';
			return;
		}
		issueSubject = `Lease agreement ${form.agreementNumber}`;
		issueConfirmationOpen = true;
	}

	function issueAgreement() {
		const draft = draftQuery.data;
		if (!draft || !issueSubject.trim()) return;
		issueMutation.mutate({ revision: draft.draftRevision, subject: issueSubject });
	}
</script>

<Dialog.Root open onOpenChange={(open) => { if (!open && !editMutation.isPending && !issueMutation.isPending) onclose(); }}>
	<Dialog.Content class="max-w-4xl" data-testid="agreement-draft-dialog">
		<Dialog.Header>
			<Dialog.Title>Edit agreement draft</Dialog.Title>
			<Dialog.Description>
				Reloaded from the canonical draft with its exact revision. Issuing freezes this version and its signer snapshots.
			</Dialog.Description>
		</Dialog.Header>

		{#if draftQuery.isLoading}
			<p class="text-sm text-muted-foreground">Loading exact draft state…</p>
		{:else if draftQuery.isError || !draftQuery.data || !form}
			<div class="rounded-xl border border-destructive/30 bg-destructive/5 p-4 text-sm">
				This agreement is no longer an editable draft. Refresh the agreement history.
			</div>
		{:else}
			{@const draft = draftQuery.data}
			<div class="space-y-5">
				<div class="flex flex-wrap gap-2 text-xs text-muted-foreground">
					<span>Version {draft.versionNumber}</span><span>·</span>
					<span>Revision {draft.draftRevision}</span><span>·</span>
					<span>Template {draft.documentTemplateId ?? 'not editable'}{draft.documentTemplateVersion ? ` v${draft.documentTemplateVersion}` : ''}</span>
				</div>

				<div class="grid gap-4 md:grid-cols-3">
					<label class="space-y-1 md:col-span-2">
						<span class="text-sm font-medium">Agreement number</span>
						<Input bind:value={form.agreementNumber} data-testid="agreement-draft-number" />
					</label>
					<label class="space-y-1">
						<span class="text-sm font-medium">Term type</span>
						<select bind:value={form.termType} class="m3-field-surface h-10 w-full px-3 text-sm" data-testid="agreement-draft-term-type">
							<option value="FixedTerm">Fixed term</option>
							<option value="MonthToMonth">Month to month</option>
						</select>
					</label>
					<label class="space-y-1">
						<span class="text-sm font-medium">Term starts</span>
						<Input type="date" bind:value={form.termStartOn} data-testid="agreement-draft-term-start" />
					</label>
					{#if form.termType === 'FixedTerm'}
						<label class="space-y-1">
							<span class="text-sm font-medium">Term ends</span>
							<Input type="date" bind:value={form.termEndOn} data-testid="agreement-draft-term-end" />
						</label>
					{/if}
					<label class="space-y-1">
						<span class="text-sm font-medium">Governs from</span>
						<Input type="date" bind:value={form.governingFromOn} data-testid="agreement-draft-governing-from" />
					</label>
				</div>

				<div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
					<label class="space-y-1"><span class="text-sm font-medium">Monthly rent</span><Input type="number" min="0" step="0.01" bind:value={form.baseRentAmount} /></label>
					<label class="space-y-1"><span class="text-sm font-medium">Due day</span><Input type="number" min="1" max="31" bind:value={form.rentDueDay} /></label>
					<label class="space-y-1"><span class="text-sm font-medium">Deposit</span><Input type="number" min="0" step="0.01" bind:value={form.securityDepositObligation} /></label>
					<label class="space-y-1"><span class="text-sm font-medium">Late fee</span><Input type="number" min="0" step="0.01" bind:value={form.lateFeeAmount} /></label>
					<label class="space-y-1"><span class="text-sm font-medium">Grace days</span><Input type="number" min="0" bind:value={form.gracePeriodDays} /></label>
				</div>

				<div class="space-y-3">
					<div><h3 class="font-medium">Signer snapshots</h3><p class="text-xs text-muted-foreground">These names and email addresses are frozen when the agreement is issued.</p></div>
					{#each form.signers as signer, index}
						<div class="grid gap-3 rounded-xl border p-3 md:grid-cols-[5rem_1fr_1fr_10rem_auto]">
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Order</span><Input type="number" min="1" bind:value={signer.signingOrder} /></label>
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Name</span><Input bind:value={signer.nameSnapshot} data-testid="agreement-draft-signer-name-{index}" /></label>
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Email</span><Input type="email" bind:value={signer.emailSnapshot} data-testid="agreement-draft-signer-email-{index}" /></label>
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Role</span><select bind:value={signer.signerRole} class="m3-field-surface h-10 w-full px-2 text-sm">{#each signerRoles as role}<option value={role}>{role}</option>{/each}</select></label>
							<p class="pt-6 text-sm text-muted-foreground">Required signer</p>
						</div>
					{/each}
				</div>

				{#if validationError}<p class="text-sm text-destructive" data-testid="agreement-draft-error">{validationError}</p>{/if}
				{#if issueConfirmationOpen}
					<div class="space-y-3 rounded-xl border border-primary/30 bg-primary/5 p-4" data-testid="agreement-issue-confirmation">
						<div><p class="font-medium">Issue this exact revision for signature?</p><p class="text-sm text-muted-foreground">The server will render and fingerprint the PDF, then freeze the agreement and signer snapshots.</p></div>
						<label class="space-y-1"><span class="text-sm font-medium">Signature request subject</span><Input bind:value={issueSubject} /></label>
						<div class="flex justify-end gap-2"><Button variant="outline" onclick={() => (issueConfirmationOpen = false)} disabled={issueMutation.isPending}>Not yet</Button><Button onclick={issueAgreement} disabled={!issueSubject.trim() || issueMutation.isPending} class="gap-2">{#if issueMutation.isPending}<Loader2 class="h-4 w-4 animate-spin" /> Preparing &amp; issuing…{:else}<FileSignature class="h-4 w-4" /> Issue for signature{/if}</Button></div>
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
