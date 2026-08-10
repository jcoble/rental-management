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
	import * as Select from '$lib/components/ui/select';
	import { Input } from '$lib/components/ui/input';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import { CalendarCheck, KeyRound, Loader2, Undo2 } from '@lucide/svelte';

	let {
		summary,
		canManage,
		onchanged
	}: {
		summary: LeaseManagementSummary;
		canManage: boolean;
		onchanged: () => void | Promise<void>;
	} = $props();

	let giveOpen = $state(false);
	let giveOperationKey = $state('');
	let historicalOpen = $state(false);
	let historicalPossessionDate = $state('');
	let historicalDateInvalid = $state(false);
	let historicalValidationError = $state('');
	let historicalOperation = $state<{ fingerprint: string; key: string } | null>(null);
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
		!summary.hasGoverningAgreementWithoutPossession &&
			!summary.possessionGivenAtUtc &&
			!summary.possessionReturnedAtUtc &&
			!summary.canceledAtUtc
	);
	const canReconcileHistoricalPossession = $derived(
		summary.hasGoverningAgreementWithoutPossession &&
			!summary.possessionGivenAtUtc &&
			!summary.possessionReturnedAtUtc &&
			!summary.canceledAtUtc
	);
	const canReturnPossession = $derived(
		Boolean(summary.possessionGivenAtUtc) && !summary.possessionReturnedAtUtc && !summary.canceledAtUtc
	);

	function closeGive() {
		if (giveMutation.isPending) return;
		giveOpen = false;
		giveOperationKey = '';
	}

	function openHistorical() {
		historicalPossessionDate =
			summary.termStartOn ?? summary.plannedPossessionAtUtc?.slice(0, 10) ?? summary.businessDate;
		historicalDateInvalid = false;
		historicalValidationError = '';
		historicalOperation = null;
		historicalOpen = true;
	}

	function closeHistorical() {
		if (historicalMutation.isPending) return;
		historicalOpen = false;
		historicalDateInvalid = false;
		historicalValidationError = '';
		historicalOperation = null;
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

	function historicalOperationKey() {
		const fingerprint = `${summary.unitId}:${historicalPossessionDate}`;
		if (!historicalOperation || historicalOperation.fingerprint !== fingerprint) {
			historicalOperation = { fingerprint, key: crypto.randomUUID() };
		}
		return historicalOperation.key;
	}

	const historicalMutation = createMutation(() => ({
		mutationFn: () =>
			leaseManagements.reconcileHistoricalPossession(
				summary.leaseManagementId,
				{ unitId: summary.unitId, possessionGivenOn: historicalPossessionDate },
				historicalOperationKey()
			),
		onSuccess: async () => {
			showSuccess('Past move-in details updated.');
			historicalOpen = false;
			historicalValidationError = '';
			historicalOperation = null;
			await onchanged();
		},
		onError: (error) => showError(apiErrorMessage(error, 'Could not update the past move-in details.'))
	}));

	function submitHistorical() {
		historicalValidationError = '';
		if (historicalDateInvalid) {
			historicalValidationError = 'Enter a valid possession date.';
			return;
		}
		if (!historicalPossessionDate) {
			historicalValidationError = 'Possession date is required.';
			return;
		}
		historicalMutation.mutate();
	}

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

	function formatBusinessDate(value: string) {
		const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
		if (!match) return value;
		const [, year, month, day] = match;
		return new Intl.DateTimeFormat(undefined, { timeZone: 'UTC' }).format(
			new Date(Date.UTC(Number(year), Number(month) - 1, Number(day)))
		);
	}
</script>

<Card data-testid="lease-possession-actions">
	<CardHeader>
		<CardTitle class="flex items-center gap-2"><KeyRound class="h-5 w-5" /> Possession</CardTitle>
		<p class="text-sm text-muted-foreground">
			Track when keys and physical access are handed over or returned.
		</p>
	</CardHeader>
	<CardContent class="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
		<div class="space-y-1 text-sm">
			{#if summary.possessionReturnedAtUtc}
				<p class="font-medium">Possession returned</p>
				<p class="text-muted-foreground">{new Date(summary.possessionReturnedAtUtc).toLocaleString()}</p>
			{:else if summary.possessionGivenAtUtc}
				<p class="font-medium">Tenant has possession</p>
				<p class="text-muted-foreground">Given {formatBusinessDate(summary.possessionGivenAtUtc)}</p>
			{:else if summary.canceledAtUtc}
				<p class="font-medium">Relationship canceled</p>
				<p class="text-muted-foreground">Possession actions are no longer available.</p>
			{:else}
				<p class="font-medium">Possession not yet given</p>
				<p class="text-muted-foreground">Use this only when keys and physical control are handed over.</p>
			{/if}
		</div>
		<div class="flex flex-wrap gap-2">
			{#if canManage && canGivePossession}
				<Button onclick={() => (giveOpen = true)} data-testid="lease-give-possession">
					<KeyRound class="mr-2 h-4 w-4" /> Give possession
				</Button>
			{/if}
			{#if canManage && canReconcileHistoricalPossession}
				<Button variant="outline" onclick={openHistorical} data-testid="lease-reconcile-historical-possession">
					<CalendarCheck class="mr-2 h-4 w-4" /> Update past move-in details
				</Button>
			{/if}
			{#if canManage && canReturnPossession}
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
			<Dialog.Description>Confirm that the tenant received the keys and physical access to unit {summary.unitNumber}. Rental Command will record the time.</Dialog.Description>
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

<Dialog.Root open={historicalOpen} onOpenChange={(open) => { if (!open) closeHistorical(); }}>
	<Dialog.Content class="max-w-md" data-testid="lease-reconcile-historical-possession-dialog">
		<Dialog.Header>
			<Dialog.Title>Update past move-in details</Dialog.Title>
			<Dialog.Description>Record the actual historical date the tenant received possession for unit {summary.unitNumber}.</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-2">
			<label for="historical-possession-date" class="text-sm font-medium">Possession date</label>
			<DatePicker
				id="historical-possession-date"
				testid="historical-possession-date"
				bind:value={historicalPossessionDate}
				bind:invalid={historicalDateInvalid}
				max={summary.businessDate}
				onchange={() => (historicalValidationError = '')}
			/>
			<p class="text-xs text-muted-foreground">The date must be inside the executed agreement term.</p>
		</div>
		{#if historicalValidationError}<p class="text-sm text-destructive">{historicalValidationError}</p>{/if}
		<Dialog.Footer>
			<Button variant="outline" onclick={closeHistorical} disabled={historicalMutation.isPending}>Cancel</Button>
			<Button onclick={submitHistorical} disabled={historicalMutation.isPending || historicalDateInvalid} data-testid="historical-possession-submit">
				{#if historicalMutation.isPending}<Loader2 class="mr-2 h-4 w-4 animate-spin" />{/if}
				Update past move-in details
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<Dialog.Root open={returnOpen} onOpenChange={(open) => { if (!open) closeReturn(); }}>
	<Dialog.Content class="max-h-[90vh] max-w-2xl overflow-y-auto" data-testid="lease-return-possession-dialog">
		<Dialog.Header>
			<Dialog.Title>Return possession</Dialog.Title>
			<Dialog.Description>Choose what happens to each household member and their login access after the keys are returned. This will also start turnover work.</Dialog.Description>
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
							<Select.Root
								type="single"
								value={partyDispositions[party.leaseManagementPartyId] ?? ''}
								onValueChange={(value) => {
									partyDispositions = { ...partyDispositions, [party.leaseManagementPartyId]: value as ReturnPossessionPartyDisposition | undefined };
									returnValidationError = '';
								}}
							>
								<Select.Trigger class="w-full" data-testid="return-party-{party.leaseManagementPartyId}">
									{partyDispositions[party.leaseManagementPartyId] === 'EndMembership'
										? 'Remove from this household'
										: partyDispositions[party.leaseManagementPartyId] === 'RetainGuarantor'
											? 'Keep as guarantor'
											: 'Choose what happens'}
								</Select.Trigger>
								<Select.Content>
									<Select.Item value="EndMembership" label="Remove from this household">Remove from this household</Select.Item>
									{#if party.role === 'Guarantor'}<Select.Item value="RetainGuarantor" label="Keep as guarantor">Keep as guarantor</Select.Item>{/if}
								</Select.Content>
							</Select.Root>
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
							<Select.Root
								type="single"
								value={accessDispositions[access.tenantUserAccessId] ?? ''}
								onValueChange={(value) => {
									accessDispositions = { ...accessDispositions, [access.tenantUserAccessId]: value as ReturnPossessionAccessDisposition | undefined };
									returnValidationError = '';
								}}
							>
								<Select.Trigger class="w-full" data-testid="return-access-{access.tenantUserAccessId}">
									{accessDispositions[access.tenantUserAccessId] === 'RevokeNow'
										? 'Remove login access now'
										: accessDispositions[access.tenantUserAccessId] === 'RetainHistorical'
											? 'Keep access to past records'
											: 'Choose what happens'}
								</Select.Trigger>
								<Select.Content>
									<Select.Item value="RevokeNow" label="Remove login access now">Remove login access now</Select.Item>
									<Select.Item value="RetainHistorical" label="Keep access to past records">Keep access to past records</Select.Item>
								</Select.Content>
							</Select.Root>
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
