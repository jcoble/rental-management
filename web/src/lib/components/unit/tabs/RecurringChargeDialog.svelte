<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { accounting } from '$lib/api/endpoints/accounting';
	import { tenantAccounts } from '$lib/api/endpoints/tenant-accounts';
	import { leaseManagements } from '$lib/api/endpoints/lease-managements';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import LedgerAmount from '$lib/components/ledger/LedgerAmount.svelte';

	let {
		open,
		tenantAccountId,
		leaseAgreementId,
		leaseManagementId,
		propertyId,
		unitId,
		onClose
	}: {
		open: boolean;
		tenantAccountId: number;
		leaseAgreementId?: number | null;
		leaseManagementId: number;
		propertyId: number;
		unitId: number;
		onClose: () => void;
	} = $props();

	const queryClient = useQueryClient();
	const today = () => new Date().toISOString().slice(0, 10);
	let operationKey = $state<string | null>(null);
	let errors = $state<Record<string, string>>({});
	let form = $state({ displayName: 'Monthly rent', amount: '', ledgerAccountId: '', monthlyDueDay: '1', effectiveStartOn: today(), effectiveEndOn: '' });

	const accountsQuery = createQuery(() => ({
		queryKey: ['accounting', 'chart-of-accounts', 'recurring-charge-income'],
		queryFn: () => accounting.chartOfAccounts({ activeOnly: true, accountTypes: 'Income', take: 500, sort: 'code' }),
		enabled: open
	}));
	const schedulesQuery = createQuery(() => ({
		queryKey: ['tenant-recurring-charges', tenantAccountId],
		queryFn: () => tenantAccounts.recurringCharges(tenantAccountId, { skip: 0, take: 100, sort: 'displayName' }),
		enabled: open
	}));
	const agreementsQuery = createQuery(() => ({
		queryKey: ['lease-management-agreements', leaseManagementId, 'recurring-charge'],
		queryFn: () => leaseManagements.agreements(leaseManagementId, { skip: 0, take: 20, sort: '-versionNumber' }),
		enabled: open && !leaseAgreementId
	}));
	const resolvedLeaseAgreementId = $derived(leaseAgreementId
		?? agreementsQuery.data?.items.find((agreement) => agreement.isGoverning)?.leaseAgreementId
		?? agreementsQuery.data?.items[0]?.leaseAgreementId
		?? null);

	$effect(() => {
		if (!open || form.ledgerAccountId || !accountsQuery.data?.items.length) return;
		const mappedDefault = accountsQuery.data.items.find((account) => account.systemKey === 'rental-income')
			?? accountsQuery.data.items.find((account) => account.code === '4000')
			?? accountsQuery.data.items[0];
		form.ledgerAccountId = String(mappedDefault.id);
	});

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['tenant-recurring-charges', tenantAccountId] });
	}

	const createScheduleMutation = createMutation(() => ({
		mutationFn: () => {
			operationKey ??= crypto.randomUUID();
			return tenantAccounts.createRecurringCharge(tenantAccountId, operationKey, {
				displayName: form.displayName.trim(),
				amount: Number(form.amount),
				ledgerAccountId: Number(form.ledgerAccountId),
				leaseAgreementId: resolvedLeaseAgreementId!,
				effectiveStartOn: form.effectiveStartOn,
				effectiveEndOn: form.effectiveEndOn || null,
				monthlyDueDay: Number(form.monthlyDueDay),
				propertyId,
				unitId
			});
		},
		onSuccess: () => {
			showSuccess('Recurring charge created.');
			operationKey = null;
			form = { displayName: 'Monthly rent', amount: '', ledgerAccountId: form.ledgerAccountId, monthlyDueDay: '1', effectiveStartOn: today(), effectiveEndOn: '' };
			invalidate();
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const activeMutation = createMutation(() => ({
		mutationFn: ({ id, isActive }: { id: number; isActive: boolean }) => {
			const key = crypto.randomUUID();
			return isActive
				? tenantAccounts.patchRecurringCharge(tenantAccountId, id, key, { isActive: true })
				: tenantAccounts.deactivateRecurringCharge(tenantAccountId, id, key);
		},
		onSuccess: (_data, variables) => {
			showSuccess(variables.isActive ? 'Recurring charge activated.' : 'Recurring charge deactivated.');
			invalidate();
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	function submit() {
		errors = {};
		if (!form.displayName.trim()) errors.displayName = 'Enter a name.';
		if (!(Number(form.amount) > 0)) errors.amount = 'Enter an amount greater than zero.';
		if (!form.ledgerAccountId) errors.ledgerAccountId = 'Choose an income account.';
		if (!resolvedLeaseAgreementId) errors.leaseAgreementId = 'This tenant account does not have a lease agreement.';
		if (!Number.isInteger(Number(form.monthlyDueDay)) || Number(form.monthlyDueDay) < 1 || Number(form.monthlyDueDay) > 31) errors.monthlyDueDay = 'Enter a day from 1 to 31.';
		if (!form.effectiveStartOn) errors.effectiveStartOn = 'Pick a start date.';
		if (form.effectiveEndOn && form.effectiveEndOn < form.effectiveStartOn) errors.effectiveEndOn = 'End date must be on or after the start date.';
		if (Object.keys(errors).length === 0) createScheduleMutation.mutate();
	}
</script>

<Dialog.Root {open} onOpenChange={(value) => { if (!value) onClose(); }}>
	<Dialog.Content class="max-h-[90dvh] overflow-y-auto sm:max-w-3xl" data-testid="recurring-charge-dialog">
		<Dialog.Header>
			<Dialog.Title>Recurring charges</Dialog.Title>
			<Dialog.Description>Create monthly obligations and manage existing schedules.</Dialog.Description>
		</Dialog.Header>

		<section class="space-y-3 rounded-xl bg-muted/30 p-4" aria-labelledby="new-recurring-charge">
			<h3 id="new-recurring-charge" class="font-semibold">New monthly charge</h3>
			<div class="grid gap-3 sm:grid-cols-2">
				<label class="text-xs font-medium text-muted-foreground">Name<Input bind:value={form.displayName} data-testid="recurring-charge-name" />{#if errors.displayName}<span class="text-destructive">{errors.displayName}</span>{/if}</label>
				<label class="text-xs font-medium text-muted-foreground">Amount<Input type="text" inputmode="decimal" mask="currency" bind:value={form.amount} data-testid="recurring-charge-amount" />{#if errors.amount}<span class="text-destructive">{errors.amount}</span>{/if}</label>
				<label class="text-xs font-medium text-muted-foreground sm:col-span-2">Income account<Select.Root type="single" bind:value={form.ledgerAccountId}><Select.Trigger class="w-full" data-testid="recurring-charge-account">{accountsQuery.data?.items.find((account) => String(account.id) === form.ledgerAccountId)?.name ?? 'Choose income account'}</Select.Trigger><Select.Content>{#each accountsQuery.data?.items ?? [] as account (account.id)}<Select.Item value={String(account.id)} label={`${account.code} · ${account.name}`}>{account.code} · {account.name}</Select.Item>{/each}</Select.Content></Select.Root>{#if errors.ledgerAccountId}<span class="text-destructive">{errors.ledgerAccountId}</span>{/if}</label>
				<label class="text-xs font-medium text-muted-foreground">Monthly due day<Input type="number" min="1" max="31" bind:value={form.monthlyDueDay} data-testid="recurring-charge-due-day" />{#if errors.monthlyDueDay}<span class="text-destructive">{errors.monthlyDueDay}</span>{/if}</label>
				<label class="text-xs font-medium text-muted-foreground">Start date<DatePicker bind:value={form.effectiveStartOn} />{#if errors.effectiveStartOn}<span class="text-destructive">{errors.effectiveStartOn}</span>{/if}</label>
				<label class="text-xs font-medium text-muted-foreground">End date <span class="font-normal">(optional)</span><DatePicker bind:value={form.effectiveEndOn} />{#if errors.effectiveEndOn}<span class="text-destructive">{errors.effectiveEndOn}</span>{/if}</label>
				<p class="self-end rounded-lg border p-3 text-sm text-muted-foreground">New schedules start active. Use the active toggle below to pause or resume them.</p>
				{#if errors.leaseAgreementId}<p class="text-sm text-destructive sm:col-span-2" role="alert">{errors.leaseAgreementId}</p>{/if}
			</div>
			<div class="flex justify-end"><Button onclick={submit} disabled={createScheduleMutation.isPending || accountsQuery.isLoading || agreementsQuery.isLoading}>{createScheduleMutation.isPending ? 'Creating…' : 'Create recurring charge'}</Button></div>
		</section>

		<section class="space-y-3" aria-labelledby="existing-recurring-charges">
			<h3 id="existing-recurring-charges" class="font-semibold">Existing schedules</h3>
			{#if schedulesQuery.isLoading}<LoadingState label="Loading recurring charges" testid="recurring-charges-loading" />
			{:else if schedulesQuery.isError}<p class="text-sm text-destructive" role="alert">Recurring charges could not be loaded.</p>
			{:else if schedulesQuery.data?.items.length === 0}<p class="text-sm text-muted-foreground">No recurring charges yet.</p>
			{:else}<ul class="divide-y overflow-hidden rounded-xl border">{#each schedulesQuery.data?.items ?? [] as schedule (schedule.id)}<li class="flex flex-wrap items-center justify-between gap-3 p-3" data-testid={`recurring-charge-row-${schedule.id}`}><div><p class="font-medium">{schedule.displayName}</p><p class="text-xs text-muted-foreground">Due day {schedule.monthlyDueDay} · Next {schedule.nextRunDate} · {schedule.isActive ? 'Active' : 'Inactive'}</p></div><div class="flex items-center gap-3"><LedgerAmount amount={schedule.amount} currency={schedule.currency} /><label class="flex items-center gap-2 text-sm"><Checkbox checked={schedule.isActive} disabled={activeMutation.isPending} onCheckedChange={(checked) => activeMutation.mutate({ id: schedule.id, isActive: checked === true })} data-testid={`recurring-charge-active-${schedule.id}`} /> Active</label></div></li>{/each}</ul>{/if}
		</section>

		<Dialog.Footer><Button variant="outline" onclick={onClose}>Close</Button></Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
