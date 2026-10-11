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

	/** Blank notice date means "they told me today" — or on the move-out day if that already passed. */
	function effectiveNoticeDate() {
		if (noticeDate) return noticeDate;
		return moveOutDate && moveOutDate < summary.businessDate ? moveOutDate : summary.businessDate;
	}

	function buildRequest(): RecordLeaseEndingDispositionRequest | null {
		validationError = '';
		if (!decisionReason.trim()) {
			validationError = 'Say in a few words why this is happening.';
			return null;
		}
		if (decisionReason.trim().length > 1000) {
			validationError = 'Keep the reason under 1,000 characters.';
			return null;
		}
		if (isMoveOut && !moveOutDate) {
			validationError = 'Pick the move-out date.';
			return null;
		}
		if (isMoveOut && moveOutDate < effectiveNoticeDate()) {
			validationError = 'The move-out date cannot be before the notice date.';
			return null;
		}
		return {
			unitId: summary.unitId,
			disposition,
			noticeGivenAtUtc: isMoveOut ? utcDate(effectiveNoticeDate()) : null,
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
			showSuccess('Move-out plan saved.');
			await onrecorded();
			onclose();
		},
		onError: (error) => showError(apiErrorMessage(error, 'The move-out plan could not be saved.'))
	}));

	function submit() {
		if (buildRequest()) mutation.mutate();
	}
</script>

<Dialog.Root open onOpenChange={(open) => { if (!open && !mutation.isPending) onclose(); }}>
	<Dialog.Content class="max-w-lg" data-testid="plan-move-out-dialog">
		<Dialog.Header>
			<Dialog.Title>Plan move-out</Dialog.Title>
			<Dialog.Description>Write down what happens when this lease ends. Saving this plan does not change the signed lease.</Dialog.Description>
		</Dialog.Header>

		<div class="space-y-4 py-2">
			<label class="space-y-1 text-sm">
				<span class="font-medium">What is happening?</span>
				<SimpleSelect bind:value={disposition} testid="plan-move-out-what" options={[
					{ value: 'Undecided', label: 'Not decided yet' },
					{ value: 'OfferRenewal', label: 'Renewing instead' },
					{ value: 'OfferMonthToMonth', label: 'Month to month' },
					{ value: 'NonRenewalMoveOut', label: 'Tenant is leaving' }
				]} />
			</label>

			{#if isMoveOut}
				<div class="grid gap-3 sm:grid-cols-2">
					<label class="space-y-1 text-sm">
						<span class="font-medium">Move-out date</span>
						<DatePicker bind:value={moveOutDate} testid="plan-move-out-date" />
					</label>
					<label class="space-y-1 text-sm">
						<span class="font-medium">Notice date <span class="font-normal text-muted-foreground">(optional)</span></span>
						<DatePicker bind:value={noticeDate} testid="plan-move-out-notice" />
						<span class="block text-xs text-muted-foreground">Leave blank if they told you today.</span>
					</label>
				</div>
			{/if}

			<label class="space-y-1 text-sm">
				<span class="font-medium">Reason</span>
				<textarea
					bind:value={decisionReason}
					rows="3"
					maxlength="1000"
					class="w-full rounded-md border bg-background px-3 py-2"
					data-testid="plan-move-out-reason"
					placeholder={isMoveOut ? 'Example: gave notice, not renewing, or agreed to leave early' : 'Example: they want to stay another year'}
				></textarea>
			</label>
			{#if validationError}<p class="text-sm text-destructive">{validationError}</p>{/if}
		</div>

		<Dialog.Footer>
			<Button variant="outline" onclick={onclose} disabled={mutation.isPending}>Cancel</Button>
			<Button onclick={submit} disabled={mutation.isPending} data-testid="plan-move-out-save">
				{#if mutation.isPending}<Loader2 class="mr-2 h-4 w-4 animate-spin" />{/if}
				Save
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
