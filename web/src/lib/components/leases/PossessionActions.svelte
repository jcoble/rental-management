<script lang="ts">
	import { createMutation } from '@tanstack/svelte-query';
	import {
		leaseManagements,
		type LeaseManagementCurrentPartiesContext,
		type ReturnPossessionAccessDisposition,
		type ReturnPossessionPartyDisposition,
		type ReturnPossessionRequest
	} from '$lib/api/endpoints/lease-managements';
	import type { LeaseManagementSummary } from '$lib/types';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Card, CardContent, CardHeader, CardTitle } from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Input } from '$lib/components/ui/input';
	import { KeyRound, Loader2, Undo2 } from '@lucide/svelte';

	let {
		summary,
		onchanged
	}: {
		summary: LeaseManagementSummary;
		onchanged: () => void | Promise<void>;
	} = $props();

	let giveOpen = $state(false);
	let giveOperationKey = $state('');
	let returnOpen = $state(false);
	let returnContext = $state<LeaseManagementCurrentPartiesContext | null>(null);
	let returnContextLoading = $state(false);
	let returnContextError = $state('');
	let partyDispositions = $state<Record<number, ReturnPossessionPartyDisposition | undefined>>({});
	let accessDispositions = $state<Record<number, ReturnPossessionAccessDisposition | undefined>>({});
	let turnoverReason = $state('');
	let returnValidationError = $state('');
	let returnOperation = $state<{ fingerprint: string; key: string } | null>(null);

	const canGivePossession = $derived(
		!summary.possessionGivenAtUtc && !summary.possessionReturnedAtUtc && !summary.canceledAtUtc
	);
	const canReturnPossession = $derived(
		Boolean(summary.possessionGivenAtUtc) && !summary.possessionReturnedAtUtc && !summary.canceledAtUtc
	);

	function closeGive() {
		if (giveMutation.isPending) return;
		giveOpen = false;
		giveOperationKey = '';
	}

	const giveMutation = createMutation(() => ({
		mutationFn: () => {
			giveOperationKey ||= crypto.randomUUID();
			return leaseManagements.givePossession(
				summary.leaseManagementId,
				{ unitId: summary.unitId },
				giveOperationKey
			);
		},
		onSuccess: async () => {
			showSuccess('Possession given.');
			giveOpen = false;
			giveOperationKey = '';
			await onchanged();
		},
		onError: (error) => showError(apiErrorMessage(error, 'Could not give possession.'))
	}));

	async function openReturn() {
		returnOpen = true;
		returnContext = null;
		returnContextError = '';
		returnContextLoading = true;
		partyDispositions = {};
		accessDispositions = {};
		turnoverReason = '';
		returnValidationError = '';
		returnOperation = null;
		try {
			returnContext = await leaseManagements.getReturnPossessionContext(
				summary.leaseManagementId
			);
		} catch (error) {
			returnContextError = apiErrorMessage(
				error,
				'Could not load the current household and portal access from the server.'
			);
		} finally {
			returnContextLoading = false;
		}
	}

	function closeReturn() {
		if (returnMutation.isPending) return;
		returnOpen = false;
		returnContext = null;
		returnOperation = null;
	}

	function buildReturnRequest(): ReturnPossessionRequest | null {
		returnValidationError = '';
		if (!returnContext || returnContext.parties.length === 0) {
			returnValidationError = 'The server did not return a current household to disposition.';
			return null;
		}
		if (!turnoverReason.trim()) {
			returnValidationError = 'Turnover reason is required.';
			return null;
		}
		if (turnoverReason.trim().length > 1000) {
			returnValidationError = 'Turnover reason cannot exceed 1,000 characters.';
			return null;
		}

		const parties: ReturnPossessionRequest['parties'] = [];
		for (const party of returnContext.parties) {
			const disposition = partyDispositions[party.leaseManagementPartyId];
			if (!disposition) {
				returnValidationError = `Choose an outcome for ${party.tenantName}.`;
				return null;
			}
			parties.push({ leaseManagementPartyId: party.leaseManagementPartyId, disposition });
		}

		const accesses: ReturnPossessionRequest['accesses'] = [];
		for (const access of returnContext.activeTenantUserAccesses) {
			const disposition = accessDispositions[access.tenantUserAccessId];
			if (!disposition) {
				returnValidationError = `Choose an access outcome for ${access.userDisplayName || access.userEmail}.`;
				return null;
			}
			accesses.push({ tenantUserAccessId: access.tenantUserAccessId, disposition });
		}

		return {
			unitId: summary.unitId,
			parties,
			accesses,
			turnoverReason: turnoverReason.trim()
		};
	}

	function operationKey(request: ReturnPossessionRequest) {
		const fingerprint = JSON.stringify(request);
		if (!returnOperation || returnOperation.fingerprint !== fingerprint) {
			returnOperation = { fingerprint, key: crypto.randomUUID() };
		}
		return returnOperation.key;
	}

	const returnMutation = createMutation(() => ({
		mutationFn: (request: ReturnPossessionRequest) =>
			leaseManagements.returnPossession(
				summary.leaseManagementId,
				request,
				operationKey(request)
			),
		onSuccess: async () => {
			showSuccess('Possession returned and turnover opened.');
			returnOpen = false;
			returnContext = null;
			returnOperation = null;
			await onchanged();
		},
		onError: (error) => showError(apiErrorMessage(error, 'Could not return possession.'))
	}));

	function submitReturn() {
		const request = buildReturnRequest();
		if (request) returnMutation.mutate(request);
	}
</script>

<Card data-testid="lease-possession-actions">
	<CardHeader>
		<CardTitle class="flex items-center gap-2"><KeyRound class="h-5 w-5" /> Possession</CardTitle>
		<p class="text-sm text-muted-foreground">
			Record the physical handoff separately from deposits, appointments, and lease paperwork.
		</p>
	</CardHeader>
	<CardContent class="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
		<div class="space-y-1 text-sm">
			{#if summary.possessionReturnedAtUtc}
				<p class="font-medium">Possession returned</p>
				<p class="text-muted-foreground">{new Date(summary.possessionReturnedAtUtc).toLocaleString()}</p>
			{:else if summary.possessionGivenAtUtc}
				<p class="font-medium">Tenant has possession</p>
				<p class="text-muted-foreground">Given {new Date(summary.possessionGivenAtUtc).toLocaleString()}</p>
			{:else if summary.canceledAtUtc}
				<p class="font-medium">Relationship canceled</p>
				<p class="text-muted-foreground">Possession actions are no longer available.</p>
			{:else}
				<p class="font-medium">Possession not yet given</p>
				<p class="text-muted-foreground">Use this only when keys and physical control are handed over.</p>
			{/if}
		</div>
		<div class="flex flex-wrap gap-2">
			{#if canGivePossession}
				<Button onclick={() => (giveOpen = true)} data-testid="lease-give-possession">
					<KeyRound class="mr-2 h-4 w-4" /> Give possession
				</Button>
			{/if}
			{#if canReturnPossession}
				<Button variant="destructive" onclick={openReturn} data-testid="lease-return-possession">
					<Undo2 class="mr-2 h-4 w-4" /> Return possession
				</Button>
			{/if}
		</div>
	</CardContent>
</Card>

<Dialog.Root open={giveOpen} onOpenChange={(open) => { if (!open) closeGive(); }}>
	<Dialog.Content class="max-w-md" data-testid="lease-give-possession-dialog">
		<Dialog.Header>
			<Dialog.Title>Give possession</Dialog.Title>
			<Dialog.Description>
				Confirm keys and physical control of unit {summary.unitNumber} have been handed to the tenant.
				The server records the authoritative time.
			</Dialog.Description>
		</Dialog.Header>
		<Dialog.Footer>
			<Button variant="outline" onclick={closeGive} disabled={giveMutation.isPending}>Cancel</Button>
			<Button onclick={() => giveMutation.mutate()} disabled={giveMutation.isPending}>
				{#if giveMutation.isPending}<Loader2 class="mr-2 h-4 w-4 animate-spin" />{/if}
				Give possession
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<Dialog.Root open={returnOpen} onOpenChange={(open) => { if (!open) closeReturn(); }}>
	<Dialog.Content class="max-h-[90vh] max-w-2xl overflow-y-auto" data-testid="lease-return-possession-dialog">
		<Dialog.Header>
			<Dialog.Title>Return possession</Dialog.Title>
			<Dialog.Description>
				Disposition the exact current household and active portal grants returned by the server. Returning possession opens turnover.
			</Dialog.Description>
		</Dialog.Header>

		{#if returnContextLoading}
			<div class="flex items-center gap-2 py-8 text-sm text-muted-foreground">
				<Loader2 class="h-4 w-4 animate-spin" /> Loading current household and access…
			</div>
		{:else if returnContextError}
			<div class="space-y-3 rounded-lg border border-destructive/30 bg-destructive/5 p-4 text-sm">
				<p class="text-destructive">{returnContextError}</p>
				<Button variant="outline" size="sm" onclick={openReturn}>Retry</Button>
			</div>
		{:else if returnContext}
			<div class="space-y-6">
				<section class="space-y-3">
					<div>
						<h3 class="font-medium">Current household</h3>
						<p class="text-sm text-muted-foreground">Choose an explicit outcome for every current member.</p>
					</div>
					{#each returnContext.parties as party (party.leaseManagementPartyId)}
						<label class="grid gap-1 sm:grid-cols-[1fr_15rem] sm:items-center">
							<span class="text-sm"><span class="font-medium">{party.tenantName}</span> · {party.role}</span>
							<select
								class="m3-field-surface h-10 w-full px-3 text-sm"
								value={partyDispositions[party.leaseManagementPartyId] ?? ''}
								onchange={(event) => {
									const value = (event.currentTarget as HTMLSelectElement).value as ReturnPossessionPartyDisposition | '';
									partyDispositions = { ...partyDispositions, [party.leaseManagementPartyId]: value || undefined };
									returnValidationError = '';
								}}
								data-testid="return-party-{party.leaseManagementPartyId}"
							>
								<option value="">Choose outcome…</option>
								<option value="EndMembership">End household membership</option>
								{#if party.role === 'Guarantor'}<option value="RetainGuarantor">Retain as guarantor</option>{/if}
							</select>
						</label>
					{/each}
				</section>

				<section class="space-y-3 border-t pt-5">
					<div>
						<h3 class="font-medium">Active portal access</h3>
						<p class="text-sm text-muted-foreground">Choose an explicit outcome for every active grant.</p>
					</div>
					{#if returnContext.activeTenantUserAccesses.length === 0}
						<p class="text-sm text-muted-foreground">There are no active tenant portal grants.</p>
					{/if}
					{#each returnContext.activeTenantUserAccesses as access (access.tenantUserAccessId)}
						<label class="grid gap-1 sm:grid-cols-[1fr_15rem] sm:items-center">
							<span class="text-sm">
								<span class="font-medium">{access.userDisplayName || access.userEmail}</span>
								<span class="block text-xs text-muted-foreground">{access.tenantName} · {access.userEmail}</span>
							</span>
							<select
								class="m3-field-surface h-10 w-full px-3 text-sm"
								value={accessDispositions[access.tenantUserAccessId] ?? ''}
								onchange={(event) => {
									const value = (event.currentTarget as HTMLSelectElement).value as ReturnPossessionAccessDisposition | '';
									accessDispositions = { ...accessDispositions, [access.tenantUserAccessId]: value || undefined };
									returnValidationError = '';
								}}
								data-testid="return-access-{access.tenantUserAccessId}"
							>
								<option value="">Choose outcome…</option>
								<option value="RevokeNow">Revoke access now</option>
								<option value="RetainHistorical">Retain historical access</option>
							</select>
						</label>
					{/each}
				</section>

				<section class="space-y-2 border-t pt-5">
					<label for="turnover-reason" class="text-sm font-medium">Turnover reason</label>
					<Input
						id="turnover-reason"
						bind:value={turnoverReason}
						maxlength={1000}
						placeholder="Keys returned after final move-out inspection"
						data-testid="return-turnover-reason"
					/>
					<p class="text-xs text-muted-foreground">The server records the possession-returned date and opens turnover.</p>
				</section>
			</div>
		{/if}

		{#if returnValidationError}<p class="text-sm text-destructive">{returnValidationError}</p>{/if}
		<Dialog.Footer>
			<Button variant="outline" onclick={closeReturn} disabled={returnMutation.isPending}>Cancel</Button>
			<Button
				variant="destructive"
				onclick={submitReturn}
				disabled={!returnContext || returnContextLoading || returnMutation.isPending}
				data-testid="return-possession-submit"
			>
				{#if returnMutation.isPending}<Loader2 class="mr-2 h-4 w-4 animate-spin" />{/if}
				Return possession
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
