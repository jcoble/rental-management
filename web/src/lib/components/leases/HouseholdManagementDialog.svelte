<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import {
		leaseManagements,
		type LeaseManagementPartyRole,
		type TenantAccessDisposition
	} from '$lib/api/endpoints/lease-managements';
	import { tenants } from '$lib/api/endpoints/tenants';
	import type { LeaseAgreementSummary, LeaseManagementParty, LeaseManagementSummary } from '$lib/types';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Input } from '$lib/components/ui/input';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Loader2 } from '@lucide/svelte';

	type Mode = 'add' | 'change' | 'end' | 'grant' | 'revoke';
	let {
		mode,
		summary,
		party = null,
		parties,
		agreements,
		accessId = null,
		onclose,
		onchanged
	}: {
		mode: Mode;
		summary: LeaseManagementSummary;
		party?: LeaseManagementParty | null;
		parties: LeaseManagementParty[];
		agreements: LeaseAgreementSummary[];
		accessId?: number | null;
		onclose: () => void;
		onchanged: () => void | Promise<void>;
	} = $props();

	const portfolioId = getAuthState().accessEnvelope?.selectedContext.portfolioId ?? 0;
	let search = $state('');
	let tenantId = $state(0);
	let role = $state<LeaseManagementPartyRole>((party?.role as LeaseManagementPartyRole) ?? 'Occupant');
	let effectiveDate = $state(mode === 'end'
		? new Date(new Date(`${summary.businessDate}T12:00:00`).getTime() - 86400000).toISOString().slice(0, 10)
		: summary.businessDate);
	let reason = $state('');
	let agreementId = $state<number | null>(null);
	let successorPartyId = $state<number | null>(null);
	let validationError = $state('');
	let operation: { fingerprint: string; key: string } | null = null;

	const currentParties = $derived(parties);
	const requiresAgreement = $derived(role !== 'Occupant' && mode !== 'grant' && mode !== 'revoke');
	const candidateQuery = createQuery(() => ({
		queryKey: ['household-tenant-candidates', summary.leaseManagementId, search],
		queryFn: () => tenants.listPage(portfolioId, {
			take: 20,
			sort: 'name',
			search: search.trim() || undefined,
			availableForLease: true,
			includeLeaseManagementId: summary.leaseManagementId
		}),
		enabled: mode === 'add' && portfolioId > 0
	}));
	const selectedCandidateLabel = $derived.by(() => {
		const candidate = candidateQuery.data?.items.find((item) => item.id === tenantId);
		return candidate ? `${candidate.firstName} ${candidate.lastName}` : 'Choose a person';
	});

	function keyFor(request: unknown) {
		const fingerprint = JSON.stringify({ mode, party: party?.leaseManagementPartyId, request });
		if (!operation || operation.fingerprint !== fingerprint) operation = { fingerprint, key: crypto.randomUUID() };
		return operation.key;
	}

	function legalBasis() {
		return {
			sameRelationshipConfirmed: true,
			agreementId: requiresAgreement ? agreementId : null,
			addendumId: null
		};
	}

	function validate() {
		validationError = '';
		if (mode === 'add' && tenantId <= 0) validationError = 'Choose an existing person.';
		else if (!reason.trim()) validationError = 'Explain why this change is being made.';
		else if (reason.trim().length > 500) validationError = 'The reason cannot exceed 500 characters.';
		else if (requiresAgreement && !agreementId) validationError = 'Choose the signed correction or restatement that authorizes this responsible-party change.';
		else if (mode === 'change' && role === 'PrimaryTenant' && !successorPartyId) validationError = 'Choose the current primary tenant whose role will change in the same handoff.';
		else if (mode === 'change' && party?.role === 'PrimaryTenant' && role !== 'PrimaryTenant' && !successorPartyId) validationError = 'Choose the household member who becomes primary in the same handoff.';
		else if (mode === 'end' && party?.role === 'PrimaryTenant' && !successorPartyId) validationError = 'Choose the household member who becomes primary.';
		return !validationError;
	}

	const mutation = createMutation(() => ({
		mutationFn: async () => {
			if (!validate()) throw new Error(validationError);
			const disposition: TenantAccessDisposition = 'RevokeImmediately';
			if (mode === 'add') {
				const request = { tenantId, role, effectiveFrom: effectiveDate, guarantorLegalNoticeEligible: false, changeReason: reason.trim(), legalBasis: legalBasis() };
				return leaseManagements.addParty(summary.leaseManagementId, request, keyFor(request));
			}
			if (!party) throw new Error('A household member is required.');
			if (mode === 'change') {
				const promoting = role === 'PrimaryTenant';
				const request = {
					newRole: role, effectiveOn: effectiveDate, guarantorLegalNoticeEligible: false,
					accessDisposition: disposition,
					companionPrimaryPartyId: successorPartyId,
					companionNewRole: successorPartyId ? (promoting ? 'CoTenant' : 'PrimaryTenant') as LeaseManagementPartyRole : null,
					companionGuarantorLegalNoticeEligible: false,
					changeReason: reason.trim(), legalBasis: legalBasis()
				};
				return leaseManagements.changePartyRole(summary.leaseManagementId, party.leaseManagementPartyId, request, keyFor(request));
			}
			if (mode === 'end') {
				const request = { effectiveThrough: effectiveDate, accessDisposition: disposition, primarySuccessorPartyId: successorPartyId, changeReason: reason.trim(), legalBasis: legalBasis() };
				return leaseManagements.endParty(summary.leaseManagementId, party.leaseManagementPartyId, request, keyFor(request));
			}
			if (mode === 'grant') return leaseManagements.grantPartyAccess(summary.leaseManagementId, party.leaseManagementPartyId, reason.trim(), keyFor({ reason }));
			if (!accessId) throw new Error('An active login grant is required.');
			return leaseManagements.revokePartyAccess(summary.leaseManagementId, party.leaseManagementPartyId, accessId, reason.trim(), keyFor({ reason, accessId }));
		},
		onSuccess: async () => {
			showSuccess(mode === 'grant' ? 'Resident activation invitation queued.' : mode === 'revoke' ? 'Resident login access revoked.' : 'Household relationship updated.');
			await onchanged();
			onclose();
		},
		onError: (error) => showError(apiErrorMessage(error, 'The household change could not be saved.'))
	}));

	const title = $derived(mode === 'add' ? 'Add household member' : mode === 'change' ? 'Change household role' : mode === 'end' ? 'End household membership' : mode === 'grant' ? 'Create resident login' : 'Revoke resident login');
</script>

<Dialog.Root open onOpenChange={(open) => { if (!open && !mutation.isPending) onclose(); }}>
	<Dialog.Content class="max-h-[90vh] max-w-2xl overflow-y-auto">
		<Dialog.Header>
			<Dialog.Title>{title}</Dialog.Title>
			<Dialog.Description>Update who lives here and who is responsible for the lease. Signed lease files stay unchanged; responsibility changes require a signed correction or replacement.</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-4 py-2">
			{#if mode === 'add'}
				<label class="space-y-1 text-sm"><span class="font-medium">Find an existing person</span><Input bind:value={search} placeholder="Search name, email, or phone" /></label>
				<label class="space-y-1 text-sm">
					<span class="font-medium">Person</span>
					<Select.Root type="single" value={String(tenantId)} onValueChange={(value) => (tenantId = Number(value ?? 0))}>
						<Select.Trigger class="w-full">{selectedCandidateLabel}</Select.Trigger>
						<Select.Content>{#each candidateQuery.data?.items ?? [] as candidate}<Select.Item value={String(candidate.id)} label={`${candidate.firstName} ${candidate.lastName}`}>{candidate.firstName} {candidate.lastName}{candidate.email ? ` · ${candidate.email}` : ''}</Select.Item>{/each}</Select.Content>
					</Select.Root>
				</label>
			{/if}
			{#if mode === 'add' || mode === 'change'}
				<label class="space-y-1 text-sm"><span class="font-medium">Role in this home</span><Select.Root type="single" bind:value={role}><Select.Trigger class="w-full">{{ PrimaryTenant: 'Primary leaseholder', CoTenant: 'Co-leaseholder', Guarantor: 'Guarantor', Occupant: 'Other occupant' }[role]}</Select.Trigger><Select.Content><Select.Item value="PrimaryTenant" label="Primary leaseholder">Primary leaseholder</Select.Item><Select.Item value="CoTenant" label="Co-leaseholder">Co-leaseholder</Select.Item><Select.Item value="Guarantor" label="Guarantor">Guarantor</Select.Item><Select.Item value="Occupant" label="Other occupant">Other occupant</Select.Item></Select.Content></Select.Root></label>
			{/if}
			{#if mode !== 'grant' && mode !== 'revoke'}
				<label class="space-y-1 text-sm"><span class="font-medium">{mode === 'end' ? 'Last day in this role' : 'Change begins'}</span><DatePicker bind:value={effectiveDate} /></label>
			{/if}
			{#if (mode === 'change' && (role === 'PrimaryTenant' || party?.role === 'PrimaryTenant')) || (mode === 'end' && party?.role === 'PrimaryTenant')}
				<label class="space-y-1 text-sm"><span class="font-medium">New primary leaseholder</span><Select.Root type="single" value={successorPartyId ? String(successorPartyId) : ''} onValueChange={(value) => (successorPartyId = value ? Number(value) : null)}><Select.Trigger class="w-full">{currentParties.find((candidate) => candidate.leaseManagementPartyId === successorPartyId)?.tenantName ?? 'Choose a household member'}</Select.Trigger><Select.Content>{#each currentParties as candidate}{#if candidate.leaseManagementPartyId !== party?.leaseManagementPartyId}<Select.Item value={String(candidate.leaseManagementPartyId)} label={candidate.tenantName}>{candidate.tenantName}</Select.Item>{/if}{/each}</Select.Content></Select.Root></label>
			{/if}
			{#if requiresAgreement}
				<label class="space-y-1 text-sm"><span class="font-medium">Signed document authorizing this change</span><Select.Root type="single" value={agreementId ? String(agreementId) : ''} onValueChange={(value) => (agreementId = value ? Number(value) : null)}><Select.Trigger class="w-full">{agreements.find((agreement) => agreement.leaseAgreementId === agreementId)?.agreementNumber ?? 'Choose a signed correction or replacement'}</Select.Trigger><Select.Content>{#each agreements as agreement}{#if (agreement.changeType === 'Correction' || agreement.changeType === 'Restatement') && agreement.executedArtifact}<Select.Item value={String(agreement.leaseAgreementId)} label={`${agreement.agreementNumber}, version ${agreement.versionNumber}`}>{agreement.agreementNumber} · version {agreement.versionNumber}</Select.Item>{/if}{/each}</Select.Content></Select.Root></label>
			{/if}
			<label class="space-y-1 text-sm"><span class="font-medium">Reason</span><textarea bind:value={reason} rows="3" maxlength="500" class="w-full rounded-md border bg-background px-3 py-2" placeholder={mode === 'grant' ? 'Why this person needs resident access' : 'What changed and why'}></textarea></label>
			{#if mode === 'grant'}<p class="rounded-lg bg-muted/50 p-3 text-sm text-muted-foreground">A passwordless resident account is prepared from this member’s email if needed. Access is limited to this relationship, and the activation invitation is queued atomically with the grant.</p>{/if}
			{#if validationError}<p class="text-sm text-destructive">{validationError}</p>{/if}
		</div>
		<Dialog.Footer><Button variant="outline" onclick={onclose} disabled={mutation.isPending}>Cancel</Button><Button onclick={() => mutation.mutate()} disabled={mutation.isPending}>{#if mutation.isPending}<Loader2 class="mr-2 h-4 w-4 animate-spin" />{/if}Confirm</Button></Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
