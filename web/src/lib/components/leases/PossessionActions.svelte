<script lang="ts">
	import { createMutation } from '@tanstack/svelte-query';
	import {
		leaseManagements,
		type LeaseManagementCurrentPartiesContext,
		type ReturnPossessionAccessDisposition,
		type ReturnPossessionPartyDisposition,
		type ReturnPossessionRequest
	} from '$lib/api/endpoints/lease-managements';
	import { defaultReturnDispositions } from '$lib/leases/moveout-defaults';
	import type { LeaseManagementSummary } from '$lib/types';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Card, CardContent, CardHeader, CardTitle } from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Input } from '$lib/components/ui/input';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import { CalendarCheck, KeyRound, Loader2, Undo2 } from '@lucide/svelte';

	/** What we send when the landlord does not bother to type a reason. */
	const DEFAULT_TURNOVER_REASON = 'Tenant moved out';

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
			showSuccess('Keys handed over.');
			giveOpen = false;
			giveOperationKey = '';
			await onchanged();
		},
		onError: (error) => showError(apiErrorMessage(error, 'The key handover could not be saved.'))
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
			historicalValidationError = 'Enter a valid move-in date.';
			return;
		}
		if (!historicalPossessionDate) {
			historicalValidationError = 'The move-in date is required.';
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
			const context = await leaseManagements.getReturnPossessionContext(
				summary.leaseManagementId
			);
			const defaults = defaultReturnDispositions(context);
			partyDispositions = defaults.parties;
			accessDispositions = defaults.accesses;
			returnContext = context;
		} catch (error) {
			returnContextError = apiErrorMessage(
				error,
				'Could not load the current tenants and app access.'
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
			returnValidationError = 'Could not find the current tenants.';
			return null;
		}
		const reason = turnoverReason.trim() || DEFAULT_TURNOVER_REASON;
		if (reason.length > 1000) {
			returnValidationError = 'Keep the note under 1,000 characters.';
			return null;
		}

		const parties: ReturnPossessionRequest['parties'] = [];
		for (const party of returnContext.parties) {
			const disposition = partyDispositions[party.leaseManagementPartyId];
			if (!disposition) {
				returnValidationError = `Choose what happens to ${party.tenantName}.`;
				return null;
			}
			parties.push({ leaseManagementPartyId: party.leaseManagementPartyId, disposition });
		}

		const accesses: ReturnPossessionRequest['accesses'] = [];
		for (const access of returnContext.activeTenantUserAccesses) {
			const disposition = accessDispositions[access.tenantUserAccessId];
			if (!disposition) {
				returnValidationError = `Choose what happens to the app sign-in for ${access.userDisplayName || access.userEmail}.`;
				return null;
			}
			accesses.push({ tenantUserAccessId: access.tenantUserAccessId, disposition });
		}

		return {
			unitId: summary.unitId,
			parties,
			accesses,
			turnoverReason: reason
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
			showSuccess('Keys returned. Turnover started.');
			returnOpen = false;
			returnContext = null;
			returnOperation = null;
			await onchanged();
		},
		onError: (error) => showError(apiErrorMessage(error, 'The move-out could not be saved.'))
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
		<CardTitle class="flex items-center gap-2"><KeyRound class="h-5 w-5" /> Keys</CardTitle>
		<p class="text-sm text-muted-foreground">
			Track when the tenant gets the keys and when they hand them back.
		</p>
	</CardHeader>
	<CardContent class="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
		<div class="space-y-1 text-sm">
			{#if summary.possessionReturnedAtUtc}
				<p class="font-medium">Keys returned</p>
				<p class="text-muted-foreground">{new Date(summary.possessionReturnedAtUtc).toLocaleString()}</p>
			{:else if summary.possessionGivenAtUtc}
				<p class="font-medium">The tenant has the keys</p>
				<p class="text-muted-foreground">Handed over {formatBusinessDate(summary.possessionGivenAtUtc)}</p>
			{:else if summary.canceledAtUtc}
				<p class="font-medium">Lease canceled</p>
				<p class="text-muted-foreground">There is nothing left to hand over.</p>
			{:else}
				<p class="font-medium">Keys not handed over yet</p>
				<p class="text-muted-foreground">Record this only when the tenant actually gets the keys.</p>
			{/if}
		</div>
		<div class="flex flex-wrap gap-2">
			{#if canManage && canGivePossession}
				<Button onclick={() => (giveOpen = true)} data-testid="lease-give-possession">
					<KeyRound class="mr-2 h-4 w-4" /> Keys handed over
				</Button>
			{/if}
			{#if canManage && canReconcileHistoricalPossession}
				<Button variant="outline" onclick={openHistorical} data-testid="lease-reconcile-historical-possession">
					<CalendarCheck class="mr-2 h-4 w-4" /> Update past move-in details
				</Button>
			{/if}
			{#if canManage && canReturnPossession}
				<Button variant="destructive" onclick={openReturn} data-testid="lease-return-possession">
					<Undo2 class="mr-2 h-4 w-4" /> Keys returned
				</Button>
			{/if}
		</div>
	</CardContent>
</Card>

<Dialog.Root open={giveOpen} onOpenChange={(open) => { if (!open) closeGive(); }}>
	<Dialog.Content class="max-w-md" data-testid="lease-give-possession-dialog">
		<Dialog.Header>
			<Dialog.Title>Keys handed over</Dialog.Title>
			<Dialog.Description>Confirm the tenant now has the keys and can get into unit {summary.unitNumber}. Rental Command records the time for you.</Dialog.Description>
		</Dialog.Header>
		<Dialog.Footer>
			<Button variant="outline" onclick={closeGive} disabled={giveMutation.isPending}>Cancel</Button>
			<Button onclick={() => giveMutation.mutate()} disabled={giveMutation.isPending}>
				{#if giveMutation.isPending}<Loader2 class="mr-2 h-4 w-4 animate-spin" />{/if}
				Record keys handed over
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<Dialog.Root open={historicalOpen} onOpenChange={(open) => { if (!open) closeHistorical(); }}>
	<Dialog.Content class="max-w-md" data-testid="lease-reconcile-historical-possession-dialog">
		<Dialog.Header>
			<Dialog.Title>Update past move-in details</Dialog.Title>
			<Dialog.Description>Record the date the tenant actually got the keys to unit {summary.unitNumber}.</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-2">
			<label for="historical-possession-date" class="text-sm font-medium">Move-in date</label>
			<DatePicker
				id="historical-possession-date"
				testid="historical-possession-date"
				bind:value={historicalPossessionDate}
				bind:invalid={historicalDateInvalid}
				max={summary.businessDate}
				onchange={() => (historicalValidationError = '')}
			/>
			<p class="text-xs text-muted-foreground">The date must be inside the signed lease term.</p>
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
			<Dialog.Title>Keys returned</Dialog.Title>
			<Dialog.Description>Record the move-out for unit {summary.unitNumber} and start the turnover work.</Dialog.Description>
		</Dialog.Header>

		{#if returnContextLoading}
			<div class="flex items-center gap-2 py-8 text-sm text-muted-foreground">
				<Loader2 class="h-4 w-4 animate-spin" /> Loading who lives here…
			</div>
		{:else if returnContextError}
			<div class="space-y-3 rounded-lg border border-destructive/30 bg-destructive/5 p-4 text-sm">
				<p class="text-destructive">{returnContextError}</p>
				<Button variant="outline" size="sm" onclick={openReturn}>Retry</Button>
			</div>
		{:else if returnContext}
			<div class="space-y-4">
				<p class="rounded-lg bg-muted/50 p-3 text-sm" data-testid="moveout-summary-line">
					Everyone on this lease moves out and their app access ends.
				</p>

				<details class="rounded-lg border" data-testid="moveout-access-changes">
					<summary class="cursor-pointer px-3 py-2 text-sm font-medium">Change what happens to their access</summary>
					<div class="space-y-6 border-t px-3 py-4">
						<section class="space-y-3">
							<div>
								<h3 class="font-medium">Who lives here</h3>
								<p class="text-sm text-muted-foreground">Everyone is moved out unless you change it here.</p>
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
											{partyDispositions[party.leaseManagementPartyId] === 'RetainGuarantor'
												? 'Stays on as guarantor'
												: 'Moves out'}
										</Select.Trigger>
										<Select.Content>
											<Select.Item value="EndMembership" label="Moves out">Moves out</Select.Item>
											{#if party.role === 'Guarantor'}<Select.Item value="RetainGuarantor" label="Stays on as guarantor">Stays on as guarantor</Select.Item>{/if}
										</Select.Content>
									</Select.Root>
								</label>
							{/each}
						</section>

						<section class="space-y-3 border-t pt-5">
							<div>
								<h3 class="font-medium">App sign-in</h3>
								<p class="text-sm text-muted-foreground">Sign-in ends for everyone unless you change it here.</p>
							</div>
							{#if returnContext.activeTenantUserAccesses.length === 0}
								<p class="text-sm text-muted-foreground">Nobody on this lease uses the tenant app.</p>
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
											{accessDispositions[access.tenantUserAccessId] === 'RetainHistorical'
												? 'Can still see their old records'
												: 'Sign-in ends now'}
										</Select.Trigger>
										<Select.Content>
											<Select.Item value="RevokeNow" label="Sign-in ends now">Sign-in ends now</Select.Item>
											<Select.Item value="RetainHistorical" label="Can still see their old records">Can still see their old records</Select.Item>
										</Select.Content>
									</Select.Root>
								</label>
							{/each}
						</section>

						<section class="space-y-2 border-t pt-5">
							<label for="turnover-reason" class="text-sm font-medium">Note <span class="font-normal text-muted-foreground">(optional)</span></label>
							<Input
								id="turnover-reason"
								bind:value={turnoverReason}
								maxlength={1000}
								placeholder={DEFAULT_TURNOVER_REASON}
								data-testid="return-turnover-reason"
							/>
						</section>
					</div>
				</details>
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
				Record keys returned
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
