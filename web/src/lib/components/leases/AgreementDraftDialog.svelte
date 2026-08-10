<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		leaseManagements,
		type EditLeaseAgreementDraftRequest,
		type IssueLeaseAgreementResponse,
		type LeaseAgreementDraftDetail,
		type LeaseLegalSignerRole
	} from '$lib/api/endpoints/lease-managements';
	import type { LeaseAgreementSummary } from '$lib/types';
	import { documentTemplates } from '$lib/api/endpoints/document-templates';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Input } from '$lib/components/ui/input';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import SimpleSelect from '$lib/components/shared/SimpleSelect.svelte';
	import { FileSignature, Loader2, Save, Trash2 } from '@lucide/svelte';

	let {
		leaseManagementId,
		leaseAgreementId,
		source = null,
		canCancel = false,
		onclose,
		onissued,
		oncanceled
	}: {
		leaseManagementId: number;
		leaseAgreementId: number;
		source?: LeaseAgreementSummary | null;
		canCancel?: boolean;
		onclose: () => void;
		onissued: (result: IssueLeaseAgreementResponse) => void | Promise<void>;
		oncanceled: () => void | Promise<void>;
	} = $props();

	type DraftForm = {
		documentTemplateId: string;
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
	const signerRoleOptions = signerRoles.map((role) => ({
		value: role,
		label: ({
			PrimaryTenant: 'Primary leaseholder',
			CoTenant: 'Co-leaseholder',
			Guarantor: 'Guarantor',
			Manager: 'Property manager',
			Owner: 'Property owner',
			Other: 'Other signer'
		} as const)[role]
	}));
	const queryClient = useQueryClient();
	let form = $state<DraftForm | null>(null);
	let seededRevision = $state(0);
	let savedFingerprint = $state('');
	let validationError = $state('');
	let issueConfirmationOpen = $state(false);
	let issueSubject = $state('');
	let cancelConfirmationOpen = $state(false);
	let cancellationReason = $state('');
	let editOperation: { fingerprint: string; key: string } | null = null;
	let issueOperation:
		| { fingerprint: string; prepareKey: string; issueKey: string }
		| null = null;
	let cancelOperation: { fingerprint: string; key: string } | null = null;
	let draftRequestsEnabled = $state(true);
	const canLoadDraft = $derived(
		draftRequestsEnabled && leaseManagementId > 0 && leaseAgreementId > 0
	);

	const draftQuery = createQuery(() => ({
		queryKey: ['lease-managements', leaseManagementId, 'agreements', leaseAgreementId, 'draft'],
		queryFn: () => leaseManagements.getAgreementDraft(leaseManagementId, leaseAgreementId),
		enabled: canLoadDraft
	}));
	const templatesQuery = createQuery(() => ({
		queryKey: ['document-templates', 'agreement-draft', 'active'],
		queryFn: () =>
			documentTemplates.listPage({
				kind: 'Lease',
				status: 'Active',
				sort: 'name',
				take: 50
			})
	}));

	function seedForm(draft: LeaseAgreementDraftDetail): DraftForm {
		return {
			documentTemplateId: draft.documentTemplateId ? String(draft.documentTemplateId) : '',
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
	const correctionComparison = $derived.by(() => {
		if (!source || !form || draftQuery.data?.changeType !== 'Correction') return null;
		const fields = [
			{ label: 'Agreement number', oldValue: source.agreementNumber, newValue: form.agreementNumber },
			{ label: 'Term type', oldValue: source.termType, newValue: form.termType },
			{ label: 'Term starts', oldValue: source.termStartOn, newValue: form.termStartOn },
			{ label: 'Term ends', oldValue: source.termEndOn ?? 'Month-to-month', newValue: form.termEndOn || 'Month-to-month' },
			{ label: 'Effective from', oldValue: source.governingFromOn, newValue: form.governingFromOn },
			{ label: 'Monthly rent', oldValue: String(source.baseRentAmount), newValue: form.baseRentAmount }
		];
		return {
			changed: fields.filter((field) => field.oldValue !== field.newValue),
			unchanged: fields.filter((field) => field.oldValue === field.newValue)
		};
	});

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
			validationError = 'Term start and effective dates are required.';
		else if (form.termType === 'FixedTerm' && !form.termEndOn)
			validationError = 'A fixed-term agreement needs an end date.';
		else if (form.termEndOn && form.termEndOn < form.termStartOn)
			validationError = 'Term end cannot be before term start.';
		else if (
			form.governingFromOn < form.termStartOn ||
			(form.termEndOn && form.governingFromOn > form.termEndOn)
		)
			validationError = 'The effective date must fall within the lease term.';
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
			documentTemplateId: form.documentTemplateId ? Number(form.documentTemplateId) : null,
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
			if (draftRequestsEnabled) await draftQuery.refetch();
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
			draftRequestsEnabled = false;
			issueConfirmationOpen = false;
			await onissued(result);
			await queryClient.invalidateQueries({ queryKey: ['lease-managements', leaseManagementId] });
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

	function cancelKey(reason: string) {
		const fingerprint = `${leaseAgreementId}:${reason.trim()}`;
		if (!cancelOperation || cancelOperation.fingerprint !== fingerprint) {
			cancelOperation = { fingerprint, key: crypto.randomUUID() };
		}
		return cancelOperation.key;
	}

	const cancelMutation = createMutation(() => ({
		mutationFn: (reason: string) =>
			leaseManagements.cancelAgreementSuccessorDraft(
				leaseManagementId,
				leaseAgreementId,
				reason.trim(),
				cancelKey(reason)
			),
		onSuccess: async () => {
			showSuccess('Successor draft canceled.');
			draftRequestsEnabled = false;
			cancelConfirmationOpen = false;
			await oncanceled();
			await queryClient.invalidateQueries({ queryKey: ['lease-managements', leaseManagementId] });
		},
		onError: (error) => showError(apiErrorMessage(error, 'Could not cancel the successor draft.'))
	}));

	function cancelSuccessorDraft() {
		validationError = '';
		if (!cancellationReason.trim()) {
			validationError = 'Explain why this successor draft is being abandoned.';
			return;
		}
		if (cancellationReason.trim().length > 1000) {
			validationError = 'The cancellation reason cannot exceed 1,000 characters.';
			return;
		}
		cancelMutation.mutate(cancellationReason);
	}
</script>

<Dialog.Root open onOpenChange={(open) => { if (!open && !editMutation.isPending && !issueMutation.isPending && !cancelMutation.isPending) onclose(); }}>
	<Dialog.Content class="max-w-4xl" data-testid="agreement-draft-dialog">
		<Dialog.Header>
			<Dialog.Title>Edit agreement draft</Dialog.Title>
			<Dialog.Description>
				Reloaded from the current draft with its exact revision. Issuing freezes this version and the signer details.
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
				{#if draft.changeType === 'Correction'}
					<div class="rounded-xl border border-primary/30 bg-primary/5 p-4 text-sm" data-testid="agreement-correction-governing-notice">
						<p class="font-medium">The old lease is still in effect</p>
						<p class="mt-1 text-muted-foreground">This replacement is only a draft. The old lease stays in effect until this correction is fully signed and completed.</p>
						{#if draft.correctionReason}<p class="mt-2"><span class="font-medium">Reason:</span> {draft.correctionReason}</p>{/if}
					</div>
				{/if}
				<div class="flex flex-wrap gap-2 text-xs text-muted-foreground">
					<span>Version {draft.versionNumber}</span><span>·</span>
					<span>Revision {draft.draftRevision}</span><span>·</span>
					<span>{draft.documentTemplateId ? `Landlord template ${draft.documentTemplateId}${draft.documentTemplateVersion ? ` v${draft.documentTemplateVersion}` : ''}` : 'Rental Command supplied lease'}</span>
				</div>
				<label class="block space-y-1">
					<span class="text-sm font-medium">Lease document</span>
					<SimpleSelect
						bind:value={form.documentTemplateId}
						options={[
							{ value: '', label: 'Rental Command lease (recommended)' },
							...(templatesQuery.data?.items ?? []).map((template) => ({
								value: String(template.id),
								label: `${template.name} · version ${template.version}`
							}))
						]}
						testid="agreement-draft-template"
					/>
					<p class="text-xs text-muted-foreground">The selected document is saved with this draft when you send it for signature.</p>
				</label>

				<div class="grid gap-4 md:grid-cols-3">
					<label class="space-y-1 md:col-span-2">
						<span class="text-sm font-medium">Agreement number</span>
						<Input bind:value={form.agreementNumber} data-testid="agreement-draft-number" />
					</label>
					<label class="space-y-1">
						<span class="text-sm font-medium">Term type</span>
						<SimpleSelect bind:value={form.termType} options={[{ value: 'FixedTerm', label: 'Fixed term' }, { value: 'MonthToMonth', label: 'Month to month' }]} testid="agreement-draft-term-type" />
					</label>
					<label class="space-y-1">
						<span class="text-sm font-medium">Term starts</span>
						<DatePicker bind:value={form.termStartOn} testid="agreement-draft-term-start" />
					</label>
					{#if form.termType === 'FixedTerm'}
						<label class="space-y-1">
							<span class="text-sm font-medium">Term ends</span>
							<DatePicker bind:value={form.termEndOn} testid="agreement-draft-term-end" />
						</label>
					{/if}
					<label class="space-y-1">
						<span class="text-sm font-medium">Governs from</span>
						<DatePicker bind:value={form.governingFromOn} testid="agreement-draft-governing-from" />
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
					<div><h3 class="font-medium">People who must sign</h3><p class="text-xs text-muted-foreground">Review these names and email addresses before sending the lease. They cannot be changed after it is sent.</p></div>
					{#each form.signers as signer, index}
						<div class="grid gap-3 rounded-xl bg-muted/30 p-3 md:grid-cols-[5rem_1fr_1fr_10rem_auto]">
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Order</span><Input type="number" min="1" bind:value={signer.signingOrder} /></label>
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Name</span><Input bind:value={signer.nameSnapshot} data-testid="agreement-draft-signer-name-{index}" /></label>
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Email</span><Input type="email" bind:value={signer.emailSnapshot} data-testid="agreement-draft-signer-email-{index}" /></label>
							<label class="space-y-1"><span class="text-xs text-muted-foreground">Role</span><SimpleSelect bind:value={signer.signerRole} options={signerRoleOptions} /></label>
							<p class="pt-6 text-sm text-muted-foreground">Required signer</p>
						</div>
					{/each}
				</div>

				{#if correctionComparison}
					<section class="space-y-3" data-testid="agreement-correction-comparison">
						<div>
							<h3 class="font-medium">Old agreement vs correction</h3>
							<p class="text-xs text-muted-foreground">Changed fields stay visible. Unchanged copied content is collapsed.</p>
						</div>
						{#if correctionComparison.changed.length === 0}
							<div class="rounded-xl border p-3 text-sm text-muted-foreground">No tracked agreement fields have changed yet.</div>
						{:else}
							{#each correctionComparison.changed as field (field.label)}
								<div class="grid gap-2 rounded-xl border border-primary/40 bg-primary/5 p-3 text-sm sm:grid-cols-[10rem_1fr_1fr]" data-testid="agreement-correction-changed-field">
									<p class="font-medium">{field.label}</p>
									<p><span class="text-xs text-muted-foreground">Old</span><br />{field.oldValue}</p>
									<p><span class="text-xs text-muted-foreground">New</span><br /><span class="font-medium text-primary">{field.newValue}</span></p>
								</div>
							{/each}
						{/if}
						{#if correctionComparison.unchanged.length > 0}
							<details class="rounded-xl border p-3 text-sm" data-testid="agreement-correction-unchanged-fields">
								<summary class="cursor-pointer font-medium">{correctionComparison.unchanged.length} unchanged copied field{correctionComparison.unchanged.length === 1 ? '' : 's'}</summary>
								<div class="mt-3 space-y-2 text-muted-foreground">
									{#each correctionComparison.unchanged as field (field.label)}<p>{field.label}: {field.newValue}</p>{/each}
								</div>
							</details>
						{/if}
					</section>
				{/if}

				{#if validationError}<p class="text-sm text-destructive" data-testid="agreement-draft-error">{validationError}</p>{/if}
				{#if issueConfirmationOpen}
					<div class="space-y-3 rounded-xl border border-primary/30 bg-primary/5 p-4" data-testid="agreement-issue-confirmation">
						<div><p class="font-medium">Issue this exact revision for signature?</p><p class="text-sm text-muted-foreground">Rental Command will prepare the PDF, then lock the lease and signer details.</p></div>
						<label class="space-y-1"><span class="text-sm font-medium">Signature request subject</span><Input bind:value={issueSubject} /></label>
						<div class="flex justify-end gap-2"><Button variant="outline" onclick={() => (issueConfirmationOpen = false)} disabled={issueMutation.isPending}>Not yet</Button><Button onclick={issueAgreement} disabled={!issueSubject.trim() || issueMutation.isPending} class="gap-2">{#if issueMutation.isPending}<Loader2 class="h-4 w-4 animate-spin" /> Preparing and issuing…{:else}<FileSignature class="h-4 w-4" /> Prepare and issue{/if}</Button></div>
					</div>
				{/if}
				{#if cancelConfirmationOpen}
					<div class="space-y-3 rounded-xl border border-destructive/30 bg-destructive/5 p-4" data-testid="agreement-cancel-draft-confirmation">
						<div><p class="font-medium">Cancel this abandoned successor draft?</p><p class="text-sm text-muted-foreground">Its source lease is unchanged and still applies. This canceled draft cannot be issued.</p></div>
						<label class="space-y-1"><span class="text-sm font-medium">Cancellation reason</span><textarea class="m3-field-surface min-h-20 w-full resize-y px-3 py-2 text-sm" bind:value={cancellationReason} maxlength="1000" data-testid="agreement-cancel-draft-reason"></textarea></label>
						<div class="flex justify-end gap-2"><Button variant="outline" onclick={() => (cancelConfirmationOpen = false)} disabled={cancelMutation.isPending}>Keep draft</Button><Button variant="destructive" onclick={cancelSuccessorDraft} disabled={!cancellationReason.trim() || cancelMutation.isPending} class="gap-2">{#if cancelMutation.isPending}<Loader2 class="h-4 w-4 animate-spin" /> Canceling…{:else}<Trash2 class="h-4 w-4" /> Cancel draft{/if}</Button></div>
					</div>
				{/if}
			</div>
		{/if}

		<Dialog.Footer>
			<Button variant="outline" onclick={onclose} disabled={editMutation.isPending || issueMutation.isPending || cancelMutation.isPending}>Close</Button>
			{#if draftQuery.data && form}
				{#if canCancel}<Button variant="destructive" class="gap-2" onclick={() => (cancelConfirmationOpen = true)} disabled={editMutation.isPending || issueMutation.isPending || cancelMutation.isPending}><Trash2 class="h-4 w-4" /> Cancel draft</Button>{/if}
				<Button variant="outline" class="gap-2" onclick={openIssueConfirmation} disabled={isDirty || editMutation.isPending || issueMutation.isPending}><FileSignature class="h-4 w-4" /> Prepare and issue</Button>
				<Button class="gap-2" onclick={saveDraft} disabled={!isDirty || editMutation.isPending || issueMutation.isPending}>{#if editMutation.isPending}<Loader2 class="h-4 w-4 animate-spin" /> Saving…{:else}<Save class="h-4 w-4" /> Save draft{/if}</Button>
			{/if}
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
