<script lang="ts">
	import { createMutation, useQueryClient } from '@tanstack/svelte-query';
	import {
		leaseAddendums,
		type LeaseAddendumDraftMutationResponse,
		type LeaseAddendumHistoryItem
	} from '$lib/api/endpoints/lease-addendums';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Input } from '$lib/components/ui/input';
	import { FilePenLine, Loader2 } from '@lucide/svelte';

	let {
		leaseManagementId,
		source,
		onclose,
		oncreated
	}: {
		leaseManagementId: number;
		source: LeaseAddendumHistoryItem;
		onclose: () => void;
		oncreated: (result: LeaseAddendumDraftMutationResponse) => void;
	} = $props();

	const queryClient = useQueryClient();
	let supersessionEffectiveOn = $state('');
	let validationError = $state('');
	let operation: { date: string; key: string } | null = null;

	function operationKey(date: string) {
		if (!operation || operation.date !== date) operation = { date, key: crypto.randomUUID() };
		return operation.key;
	}

	const mutation = createMutation(() => ({
		mutationFn: (date: string) => leaseAddendums.correctDraft(
			leaseManagementId,
			source.leaseAddendumId,
			date,
			operationKey(date)
		),
		onSuccess: async (result) => {
			showSuccess('Addendum correction draft created.');
			await queryClient.invalidateQueries({ queryKey: ['lease-managements', leaseManagementId] });
			oncreated(result);
		},
		onError: (error) => showError(apiErrorMessage(error, 'Could not create the correction draft.'))
	}));

	function submit() {
		validationError = '';
		if (!supersessionEffectiveOn) validationError = 'Supersession date is required.';
		else if (supersessionEffectiveOn <= source.effectiveFromOn)
			validationError = 'The correction must begin after the source addendum effective date.';
		else if (source.effectiveThroughOn && supersessionEffectiveOn > source.effectiveThroughOn)
			validationError = 'The correction must begin within the source addendum effective range.';
		if (!validationError) mutation.mutate(supersessionEffectiveOn);
	}
</script>

<Dialog.Root open onOpenChange={(open) => { if (!open && !mutation.isPending) onclose(); }}>
	<Dialog.Content class="max-w-lg" data-testid="addendum-correction-dialog">
		<Dialog.Header>
			<Dialog.Title>Correct {source.addendumNumber}</Dialog.Title>
			<Dialog.Description>Creates a new editable version in series {source.seriesPublicId}. The executed source remains immutable and governs until the correction is fully signed.</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-4">
			<div class="rounded-xl border p-4 text-sm"><p class="font-medium">Source version {source.versionNumber}</p><p class="text-muted-foreground">Effective {source.effectiveFromOn}{source.effectiveThroughOn ? ` through ${source.effectiveThroughOn}` : ' onward'}</p></div>
			<label class="space-y-1"><span class="text-sm font-medium">Correction begins</span><Input type="date" bind:value={supersessionEffectiveOn} data-testid="addendum-correction-effective-on" /></label>
			<p class="text-xs text-muted-foreground">The server copies the exact terms, signer snapshots, financial effects, and document source into the new draft.</p>
			{#if validationError}<p class="text-sm text-destructive">{validationError}</p>{/if}
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={onclose} disabled={mutation.isPending}>Cancel</Button>
			<Button class="gap-2" onclick={submit} disabled={mutation.isPending}>{#if mutation.isPending}<Loader2 class="h-4 w-4 animate-spin" /> Creating…{:else}<FilePenLine class="h-4 w-4" /> Create correction draft{/if}</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
