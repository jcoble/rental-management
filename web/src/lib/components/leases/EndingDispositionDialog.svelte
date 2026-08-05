<script lang="ts">
	import { createMutation } from '@tanstack/svelte-query';
	import {
		leaseManagements,
		type RecordLeaseEndingDispositionRequest
	} from '$lib/api/endpoints/lease-managements';
	import type { LeaseManagementSummary, LeaseManagementEndingDisposition } from '$lib/types';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import SimpleSelect from '$lib/components/shared/SimpleSelect.svelte';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Loader2 } from '@lucide/svelte';

	let {
		summary,
		onclose,
		onrecorded
	}: {
		summary: LeaseManagementSummary;
		onclose: () => void;
		onrecorded: () => void | Promise<void>;
	} = $props();

	let disposition = $state<LeaseManagementEndingDisposition>(summary.endingDisposition);
	let noticeDate = $state(summary.noticeGivenAtUtc?.slice(0, 10) ?? '');
	let moveOutDate = $state(summary.plannedMoveOutAtUtc?.slice(0, 10) ?? '');
	let decisionReason = $state('');
	let validationError = $state('');
	let operation: { fingerprint: string; key: string } | null = null;

	const isMoveOut = $derived(disposition === 'NonRenewalMoveOut');

	function utcDate(value: string) {
		return `${value}T12:00:00.000Z`;
	}

	function buildRequest(): RecordLeaseEndingDispositionRequest | null {
		validationError = '';
		if (!decisionReason.trim()) {
			validationError = 'Explain why this ending decision is being recorded.';
			return null;
		}
		if (decisionReason.trim().length > 1000) {
			validationError = 'The decision reason cannot exceed 1,000 characters.';
			return null;
		}
		if (isMoveOut && (!noticeDate || !moveOutDate)) {
			validationError = 'Move-out requires both the notice date and effective move-out date.';
			return null;
		}
		if (isMoveOut && moveOutDate < noticeDate) {
			validationError = 'The move-out date cannot be before the notice date.';
			return null;
		}
		return {
			unitId: summary.unitId,
			disposition,
			noticeGivenAtUtc: isMoveOut ? utcDate(noticeDate) : null,
			plannedMoveOutAtUtc: isMoveOut ? utcDate(moveOutDate) : null,
			decisionReason: decisionReason.trim()
		};
	}

	function operationKey(request: RecordLeaseEndingDispositionRequest) {
		const fingerprint = JSON.stringify(request);
		if (!operation || operation.fingerprint !== fingerprint) {
			operation = { fingerprint, key: crypto.randomUUID() };
		}
		return operation.key;
	}

	const mutation = createMutation(() => ({
		mutationFn: async () => {
			const request = buildRequest();
			if (!request) throw new Error(validationError);
			return leaseManagements.recordEndingDisposition(
				summary.leaseManagementId,
				request,
				operationKey(request)
			);
		},
		onSuccess: async () => {
			showSuccess('Lease ending plan recorded.');
			await onrecorded();
			onclose();
		},
		onError: (error) => showError(apiErrorMessage(error, 'Ending plan could not be recorded.'))
	}));

	function submit() {
		if (buildRequest()) mutation.mutate();
	}
</script>

<Dialog.Root open onOpenChange={(open) => { if (!open && !mutation.isPending) onclose(); }}>
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>Record lease ending plan</Dialog.Title>
			<Dialog.Description>Choose whether the tenants will renew, continue month to month, or move out. This plan does not change the signed lease.</Dialog.Description>
		</Dialog.Header>

		<div class="space-y-4 py-2">
			<label class="space-y-1 text-sm">
				<span class="font-medium">Decision</span>
				<SimpleSelect bind:value={disposition} options={[
					{ value: 'Undecided', label: 'Not decided yet' },
					{ value: 'OfferRenewal', label: 'Renew / continue with a new fixed term' },
					{ value: 'OfferMonthToMonth', label: 'Continue month to month' },
					{ value: 'NonRenewalMoveOut', label: 'Move out / end the relationship' }
				]} />
			</label>

			{#if isMoveOut}
				<p class="rounded-lg bg-muted/50 p-3 text-sm text-muted-foreground">
					Use this for non-renewal, notice to move out, or an early termination. Returning possession remains a separate final action.
				</p>
				<div class="grid gap-3 sm:grid-cols-2">
					<label class="space-y-1 text-sm">
						<span class="font-medium">Notice date</span>
						<DatePicker bind:value={noticeDate} />
					</label>
					<label class="space-y-1 text-sm">
						<span class="font-medium">Effective move-out date</span>
						<DatePicker bind:value={moveOutDate} />
					</label>
				</div>
			{/if}

			<label class="space-y-1 text-sm">
				<span class="font-medium">Decision reason</span>
				<textarea
					bind:value={decisionReason}
					rows="3"
					maxlength="1000"
					class="w-full rounded-md border bg-background px-3 py-2"
					placeholder={isMoveOut ? 'Example: tenant gave notice, non-renewal, or agreed early termination' : 'Why this continuation path was chosen'}
				></textarea>
			</label>
			{#if validationError}<p class="text-sm text-destructive">{validationError}</p>{/if}
		</div>

		<Dialog.Footer>
			<Button variant="outline" onclick={onclose} disabled={mutation.isPending}>Cancel</Button>
			<Button onclick={submit} disabled={mutation.isPending}>
				{#if mutation.isPending}<Loader2 class="mr-2 h-4 w-4 animate-spin" />{/if}
				Record ending plan
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
