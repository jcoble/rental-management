<script lang="ts">
	import { createMutation } from '@tanstack/svelte-query';
	import { leaseManagements, type LeaseAgreementDraftMutationResponse } from '$lib/api/endpoints/lease-managements';
	import type { LeaseAgreementSummary } from '$lib/types';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Loader2, RotateCcw } from '@lucide/svelte';

	let {
		leaseManagementId,
		source,
		onclose,
		oncreated
	}: {
		leaseManagementId: number;
		source: LeaseAgreementSummary;
		onclose: () => void;
		oncreated: (result: LeaseAgreementDraftMutationResponse) => void;
	} = $props();

	let voidNote = $state('');
	let reissueReason = $state('');
	let validationError = $state('');
	const alreadyVoided = Boolean(source.voidedAtUtc);
	const operationKey = crypto.randomUUID();

	const mutation = createMutation(() => ({
		mutationFn: async () =>
			leaseManagements.replaceIssuedAgreementWithDraft(
				leaseManagementId,
				source.leaseAgreementId,
				{
					voidNote: alreadyVoided ? null : (voidNote.trim() || null),
					reissueReason: reissueReason.trim()
				},
				operationKey
			),
		onSuccess: (result) => {
			showSuccess(alreadyVoided
				? 'Replacement draft created.'
				: 'Issued agreement voided and replacement draft created.');
			oncreated(result);
		},
		onError: (error) => showError(apiErrorMessage(error,
			'The agreement was not changed. Retry the atomic replacement action with the same request key.'))
	}));

	function submit() {
		validationError = '';
		if (!reissueReason.trim()) {
			validationError = 'Explain why this document must be reissued.';
			return;
		}
		if (voidNote.trim().length > 2000 || reissueReason.trim().length > 1000) {
			validationError = 'The void note or reissue reason is too long.';
			return;
		}
		mutation.mutate();
	}
</script>

<Dialog.Root open onOpenChange={(open) => { if (!open && !mutation.isPending) onclose(); }}>
	<Dialog.Content class="max-w-xl" data-testid="agreement-issued-recovery-dialog">
		<Dialog.Header>
			<Dialog.Title>{alreadyVoided ? 'Create replacement draft' : 'Void and replace issued agreement'}</Dialog.Title>
			<Dialog.Description>
				The issued PDF, content hash, signer snapshot, and audit history remain attached to {source.agreementNumber}. The new version keeps the same agreement type and does not govern until fully executed.
			</Dialog.Description>
		</Dialog.Header>

		<div class="space-y-4">
			{#if !alreadyVoided}
				<label class="space-y-1">
					<span class="text-sm font-medium">Void note</span>
					<textarea class="m3-field-surface min-h-20 w-full resize-y px-3 py-2 text-sm" bind:value={voidNote} maxlength="2000" data-testid="agreement-void-note"></textarea>
				</label>
			{/if}
			<label class="space-y-1">
				<span class="text-sm font-medium">Why this document must be reissued</span>
				<textarea class="m3-field-surface min-h-24 w-full resize-y px-3 py-2 text-sm" bind:value={reissueReason} maxlength="1000" placeholder="Explain why the issued document cannot be used" data-testid="agreement-reissue-reason"></textarea>
			</label>
			{#if validationError}<p class="text-sm text-destructive">{validationError}</p>{/if}
		</div>

		<Dialog.Footer>
			<Button variant="outline" disabled={mutation.isPending} onclick={onclose}>Cancel</Button>
			<Button disabled={mutation.isPending} onclick={submit} data-testid="agreement-recover-submit">
				{#if mutation.isPending}<Loader2 class="mr-2 h-4 w-4 animate-spin" />{:else}<RotateCcw class="mr-2 h-4 w-4" />{/if}
				{alreadyVoided ? 'Create replacement draft' : 'Void and create replacement'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
