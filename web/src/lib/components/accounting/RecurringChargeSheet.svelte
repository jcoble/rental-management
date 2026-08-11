<script lang="ts">
	import { createMutation } from '@tanstack/svelte-query';
	import {
		tenantLedgers,
		type RecurringTenantChargeRow
	} from '$lib/api/endpoints/tenant-ledgers';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { formatAccountingCurrency, formatAccountingDate } from '$lib/accounting/accounting-display';
	import AccountPicker from './AccountPicker.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import { ACCOUNTING_HELP } from '$lib/accounting/accounting-help';
	import { businessDateOrToday } from '$lib/utils/business-date';

	let {
		open,
		tenantAccountId,
		leaseAgreementId = null,
		propertyId = null,
		unitId = null,
		schedule = null,
		businessDate = null,
		onclose,
		onsaved
	}: {
		open: boolean;
		tenantAccountId: number;
		leaseAgreementId?: number | null;
		propertyId?: number | null;
		unitId?: number | null;
		schedule?: RecurringTenantChargeRow | null;
		businessDate?: string | null;
		onclose: () => void;
		onsaved: () => void;
	} = $props();

	const moneyDate = $derived(businessDateOrToday(businessDate));
	let form = $state({
		displayName: '',
		amount: '',
		ledgerAccountId: null as number | null,
		effectiveStartOn: businessDateOrToday(undefined),
		effectiveEndOn: '',
		monthlyDueDay: '1'
	});
	let errors = $state<Record<string, string>>({});
	let initializedKey = $state('');
	let operationKey = $state<string | null>(null);
	const dueDayOptions = Array.from({ length: 31 }, (_, index) => {
		const day = index + 1;
		const suffix = day % 10 === 1 && day !== 11 ? 'st' : day % 10 === 2 && day !== 12 ? 'nd' : day % 10 === 3 && day !== 13 ? 'rd' : 'th';
		return { value: String(day), label: `${day}${suffix}` };
	});

	$effect(() => {
		const nextKey = open
			? `${schedule?.id ?? 'new'}|${schedule?.nextRunDate ?? ''}`
			: '';
		if (open && nextKey !== initializedKey) {
			form = {
				displayName: schedule?.displayName ?? '',
				amount: schedule ? String(schedule.amount) : '',
				ledgerAccountId: schedule?.ledgerAccountId ?? null,
				effectiveStartOn: schedule ? schedule.effectiveStartOn : moneyDate,
				effectiveEndOn: schedule?.effectiveEndOn ?? '',
				monthlyDueDay: String(schedule?.monthlyDueDay ?? 1)
			};
			errors = {};
			operationKey = null;
			initializedKey = nextKey;
		}
		if (!open) initializedKey = '';
	});

	function close(): void {
		if (!mutation.isPending) onclose();
	}

	function validate(): boolean {
		errors = {};
		if (!form.displayName.trim()) errors.displayName = 'Name this recurring charge.';
		if (!(Number(form.amount) > 0)) errors.amount = 'Enter an amount greater than zero.';
		if (!form.ledgerAccountId) errors.category = 'Choose an income category.';
		if (!form.effectiveStartOn) errors.start = 'Pick the start date.';
		if (form.effectiveEndOn && form.effectiveEndOn < form.effectiveStartOn) {
			errors.end = 'End date must be on or after the start date.';
		}
		const dueDay = Number(form.monthlyDueDay);
		if (!Number.isInteger(dueDay) || dueDay < 1 || dueDay > 31) errors.dueDay = 'Use a day from 1 through 31.';
		return Object.keys(errors).length === 0;
	}

	const mutation = createMutation(() => ({
		mutationFn: () => {
			operationKey ??= crypto.randomUUID();
			const body = {
				displayName: form.displayName.trim(),
				amount: Number(form.amount),
				ledgerAccountId: form.ledgerAccountId as number,
				leaseAgreementId,
				effectiveStartOn: form.effectiveStartOn,
				effectiveEndOn: form.effectiveEndOn || null,
				monthlyDueDay: Number(form.monthlyDueDay),
				nextRunDate: form.effectiveStartOn,
				propertyId,
				unitId
			};
			return schedule
				? tenantLedgers.patchRecurringCharge(tenantAccountId, schedule.id, operationKey, body)
				: tenantLedgers.createRecurringCharge(tenantAccountId, operationKey, body);
		},
		onSuccess: () => {
			showSuccess(schedule ? 'Recurring charge updated.' : 'Recurring charge added.');
			onsaved();
			onclose();
		},
		onError: (error) => showError(apiErrorMessage(error, 'The recurring charge was not saved.'))
	}));
</script>

<Dialog.Root open={open} onOpenChange={(next) => { if (!next) close(); }}>
	<Dialog.Content class="max-w-xl" data-testid="recurring-charge-sheet">
		<Dialog.Header>
			<Dialog.Title>{schedule ? 'Edit recurring charge' : 'Recurring charge'}</Dialog.Title>
			<div class="flex items-start gap-1.5">
				<Dialog.Description>Set up the charge that should be created for future periods.</Dialog.Description>
				<HelpPopover
					title={ACCOUNTING_HELP.recurringCharge.title}
					summary={ACCOUNTING_HELP.recurringCharge.summary}
					learnMoreUrl={ACCOUNTING_HELP.recurringCharge.href}
					testid="recurring-charge-help"
				/>
			</div>
		</Dialog.Header>

		<div class="grid gap-4 sm:grid-cols-2">
			<label class="space-y-1 text-sm font-medium sm:col-span-2" for="recurring-charge-name">
				<span>Name</span>
				<Input id="recurring-charge-name" bind:value={form.displayName} placeholder="Monthly rent" />
				{#if errors.displayName}<span class="block text-xs font-normal text-destructive">{errors.displayName}</span>{/if}
			</label>
			<label class="space-y-1 text-sm font-medium" for="recurring-charge-amount">
				<span>Amount</span>
				<Input id="recurring-charge-amount" type="text" inputmode="decimal" mask="currency" bind:value={form.amount} placeholder="0.00" />
				{#if errors.amount}<span class="block text-xs font-normal text-destructive">{errors.amount}</span>{/if}
			</label>
			<div class="space-y-1 text-sm font-medium">
				<span>Category</span>
				<AccountPicker bind:selectedAccountId={form.ledgerAccountId} allowAll={false} types={['Income']} />
				{#if errors.category}<span class="block text-xs font-normal text-destructive">{errors.category}</span>{/if}
			</div>
			<label class="space-y-1 text-sm font-medium" for="recurring-charge-start">
				<span>Starts</span>
				<DatePicker id="recurring-charge-start" testid="recurring-charge-start" bind:value={form.effectiveStartOn} todayValue={moneyDate} />
				{#if errors.start}<span class="block text-xs font-normal text-destructive">{errors.start}</span>{/if}
			</label>
			<label class="space-y-1 text-sm font-medium" for="recurring-charge-end">
				<span>Ends <span class="font-normal text-muted-foreground">(optional)</span></span>
				<DatePicker id="recurring-charge-end" testid="recurring-charge-end" bind:value={form.effectiveEndOn} todayValue={moneyDate} />
				{#if errors.end}<span class="block text-xs font-normal text-destructive">{errors.end}</span>{/if}
			</label>
			<div class="space-y-1 text-sm font-medium">
				<span>Day due</span>
				<Select.Root type="single" bind:value={form.monthlyDueDay}>
					<Select.Trigger class="w-full">{dueDayOptions.find((option) => option.value === form.monthlyDueDay)?.label ?? 'Choose a day'}</Select.Trigger>
					<Select.Content>
						{#each dueDayOptions as option}<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>{/each}
					</Select.Content>
				</Select.Root>
				{#if errors.dueDay}<span class="block text-xs font-normal text-destructive">{errors.dueDay}</span>{/if}
			</div>
		</div>

		<div class="mt-4 rounded-lg border border-border bg-muted/20 px-3 py-2 text-sm text-muted-foreground">
			Changes apply to future charges only. Already-posted charges stay as-is.
			{#if schedule}<span class="block text-xs">Current next run: {formatAccountingDate(schedule.nextRunDate)} · {formatAccountingCurrency(schedule.amount, schedule.currency)}</span>{/if}
		</div>

		<Dialog.Footer>
			<Button variant="outline" onclick={close} disabled={mutation.isPending}>Cancel</Button>
			<Button onclick={() => validate() && mutation.mutate()} disabled={mutation.isPending} data-testid="recurring-charge-submit">
				{mutation.isPending ? 'Saving…' : 'Save'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
