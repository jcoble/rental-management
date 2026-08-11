<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import { accountingBooks } from '$lib/api/endpoints/accounting-books';
	import { tenantMoney } from '$lib/api/endpoints/tenant-money';
	import {
		TENANT_CHARGE_TYPES,
		tenantChargeType,
		type TenantChargeType
	} from '$lib/components/unit/money';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import AccountPicker from './AccountPicker.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import RangeDatePicker from '$lib/components/shared/RangeDatePicker.svelte';
	import { datePickerSubmitDisabled } from '$lib/components/shared/date-picker-state';
	import { SERVICE_PERIOD_SECTION_CLASS } from './one-time-charge-layout';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import { ACCOUNTING_HELP } from '$lib/accounting/accounting-help';
	import { formatAccountingCurrency } from '$lib/accounting/accounting-display';
	import { businessDateOrToday } from '$lib/utils/business-date';

	export interface OneTimeChargeSeed {
		description?: string;
		effectiveOn?: string;
		dueOn?: string;
		chargeType?: TenantChargeType;
	}

	let {
		open,
		tenantAccountId,
		tenantName = '',
		propertyName = '',
		unitLabel = '',
		currentBalance = null,
		currency = 'USD',
		seed = null,
		businessDate = null,
		onclose,
		onsaved
	}: {
		open: boolean;
		tenantAccountId: number;
		tenantName?: string;
		propertyName?: string;
		unitLabel?: string;
		currentBalance?: number | null;
		currency?: string;
		seed?: OneTimeChargeSeed | null;
		businessDate?: string | null;
		onclose: () => void;
		onsaved: () => void;
	} = $props();

	const moneyDate = $derived(businessDateOrToday(businessDate));
	let form = $state({
		chargeType: 'Other' as TenantChargeType,
		amount: '',
		effectiveOn: businessDateOrToday(undefined),
		dueOn: businessDateOrToday(undefined),
		servicePeriodStartOn: '',
		servicePeriodEndOn: '',
		description: '',
		categoryAccountId: null as number | null
	});
	let errors = $state<Record<string, string>>({});
	let effectiveDateInvalid = $state(false);
	let dueDateInvalid = $state(false);
	let servicePeriodInvalid = $state(false);
	let documentName = $state('');
	let initializedKey = $state('');
	let operationKey = $state<string | null>(null);

	const accountsQuery = createQuery(() => ({
		queryKey: ['accounting-chart-of-accounts', 'active', 'tenant-charge'],
		enabled: open && tenantAccountId > 0,
		queryFn: () => accountingBooks.chartOfAccounts({ activeOnly: true })
	}));
	const accounts = $derived(accountsQuery.data?.items ?? []);
	const chargeMapping = $derived(tenantChargeType(form.chargeType));
	const datePickerInvalid = $derived(
		effectiveDateInvalid || dueDateInvalid || servicePeriodInvalid
	);
	const resolvedAccount = $derived.by(() => {
		if (chargeMapping.systemKey) return accounts.find((account) => account.systemKey === chargeMapping.systemKey) ?? null;
		return accounts.find((account) => account.id === form.categoryAccountId) ?? null;
	});
	const targetLabel = $derived(
		[tenantName || 'Tenant account', propertyName, unitLabel].filter(Boolean).join(' · ')
	);
	const projectedBalance = $derived(
		currentBalance == null || !form.amount.trim() || !Number.isFinite(Number(form.amount))
			? null
			: currentBalance + Number(form.amount || 0)
	);

	$effect(() => {
		const nextKey = open
			? `${moneyDate}|${seed?.description ?? ''}|${seed?.effectiveOn ?? ''}|${seed?.dueOn ?? ''}|${seed?.chargeType ?? ''}`
			: '';
		if (open && nextKey !== initializedKey) {
			form = {
				chargeType: seed?.chargeType ?? 'Other',
				amount: '',
				effectiveOn: seed?.effectiveOn ?? moneyDate,
				dueOn: seed?.dueOn ?? seed?.effectiveOn ?? moneyDate,
				servicePeriodStartOn: '',
				servicePeriodEndOn: '',
				description: seed?.description ?? '',
				categoryAccountId: null
			};
			errors = {};
			effectiveDateInvalid = false;
			dueDateInvalid = false;
			servicePeriodInvalid = false;
			documentName = '';
			operationKey = null;
			initializedKey = nextKey;
		}
		if (!open) initializedKey = '';
	});

	function close(): void {
		if (!mutation.isPending) onclose();
	}

	function selectDocument(event: Event): void {
		const file = (event.currentTarget as HTMLInputElement).files?.[0];
		documentName = file?.name ?? '';
	}

	function validate(): boolean {
		errors = {};
		if (!(Number(form.amount) > 0)) errors.amount = 'Enter an amount greater than zero.';
		if (effectiveDateInvalid || !form.effectiveOn) errors.effectiveOn = 'Pick a valid effective date.';
		if (dueDateInvalid || !form.dueOn) errors.dueOn = 'Pick a valid due date.';
		if (!form.description.trim()) errors.description = 'Describe this charge.';
		if (servicePeriodInvalid) {
			errors.servicePeriod = 'Enter valid service-period dates.';
		} else if ((form.servicePeriodStartOn && !form.servicePeriodEndOn) || (!form.servicePeriodStartOn && form.servicePeriodEndOn)) {
			errors.servicePeriod = 'Enter both service-period dates or leave both empty.';
		} else if (form.servicePeriodStartOn && form.servicePeriodEndOn && form.servicePeriodEndOn < form.servicePeriodStartOn) {
			errors.servicePeriod = 'Service-period end must be on or after the start.';
		}
		if (form.chargeType === 'Other' && !form.categoryAccountId) errors.category = 'Choose an income category.';
		if (form.chargeType !== 'Other' && accountsQuery.isError) errors.category = 'The matching income category is unavailable.';
		if (form.chargeType !== 'Other' && !resolvedAccount && !accountsQuery.isLoading) errors.category = 'The matching income category is unavailable.';
		return Object.keys(errors).length === 0;
	}

	const mutation = createMutation(() => ({
		mutationFn: () => {
			operationKey ??= crypto.randomUUID();
			return tenantMoney.postCharge(tenantAccountId, operationKey, {
				amount: Number(form.amount),
				effectiveOn: form.effectiveOn,
				dueOn: form.dueOn,
				description: form.description.trim(),
				incomeLedgerAccountId: resolvedAccount?.id ?? null,
				servicePeriodStartOn: form.servicePeriodStartOn || null,
				servicePeriodEndOn: form.servicePeriodEndOn || null
			});
		},
		onSuccess: () => {
			showSuccess('Charge added.');
			onsaved();
			onclose();
		},
		onError: (error) => showError(apiErrorMessage(error, 'The charge was not added.'))
	}));
</script>

<Dialog.Root open={open} onOpenChange={(next) => { if (!next) close(); }}>
	<Dialog.Content class="max-w-2xl" data-testid="one-time-charge-sheet">
		<Dialog.Header>
			<Dialog.Title>Add one-time charge</Dialog.Title>
			<div class="flex items-start gap-1.5">
				<div class="min-w-0 space-y-1">
					<Dialog.Description>This creates a real rent or resident charge for this tenant account.</Dialog.Description>
					<p class="text-sm font-medium text-foreground" data-testid="one-time-charge-target">{targetLabel}</p>
					{#if projectedBalance != null}
						<p class="text-xs text-muted-foreground" data-testid="one-time-charge-projected-balance">New balance {formatAccountingCurrency(projectedBalance, currency)}</p>
					{/if}
				</div>
				<HelpPopover
					title={ACCOUNTING_HELP.oneTimeCharge.title}
					summary={ACCOUNTING_HELP.oneTimeCharge.summary}
					learnMoreUrl={ACCOUNTING_HELP.oneTimeCharge.href}
					testid="one-time-charge-help"
					side="right"
				/>
			</div>
		</Dialog.Header>

		<div class="grid gap-4 sm:grid-cols-2">
			<label class="space-y-1 text-sm font-medium sm:col-span-2" for="one-time-charge-type">
				<span>Charge type</span>
				<Select.Root type="single" bind:value={form.chargeType}>
					<Select.Trigger id="one-time-charge-type" class="w-full" data-testid="one-time-charge-type">{chargeMapping.label}</Select.Trigger>
					<Select.Content>{#each TENANT_CHARGE_TYPES as option}<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>{/each}</Select.Content>
				</Select.Root>
			</label>
			<label class="space-y-1 text-sm font-medium" for="one-time-charge-amount">
				<span>Amount</span>
				<Input id="one-time-charge-amount" type="text" inputmode="decimal" mask="currency" bind:value={form.amount} placeholder="0.00" autofocus data-testid="one-time-charge-amount" />
				{#if errors.amount}<span class="block text-xs font-normal text-destructive">{errors.amount}</span>{/if}
			</label>
			<label class="space-y-1 text-sm font-medium" for="one-time-charge-effective-date">
				<span>Effective date</span>
				<DatePicker
					id="one-time-charge-effective-date"
					testid="one-time-charge-effective-date"
					bind:value={form.effectiveOn}
					todayValue={moneyDate}
					bind:invalid={effectiveDateInvalid}
				/>
				{#if errors.effectiveOn}<span class="block text-xs font-normal text-destructive">{errors.effectiveOn}</span>{/if}
			</label>
			<label class="space-y-1 text-sm font-medium" for="one-time-charge-due-date">
				<span>Due date</span>
				<DatePicker
					id="one-time-charge-due-date"
					testid="one-time-charge-due-date"
					bind:value={form.dueOn}
					todayValue={moneyDate}
					bind:invalid={dueDateInvalid}
				/>
				{#if errors.dueOn}<span class="block text-xs font-normal text-destructive">{errors.dueOn}</span>{/if}
			</label>
			<div class={SERVICE_PERIOD_SECTION_CLASS}>
				<div class="flex flex-wrap items-baseline justify-between gap-2">
					<span>What period does this cover? <span class="font-normal text-muted-foreground">(optional)</span></span>
				</div>
				<p class="text-xs font-normal text-muted-foreground">The range describes the months this charge covers for reporting; it does not change the due date.</p>
				<RangeDatePicker
					id="one-time-charge-service-period"
					testid="one-time-charge-service-period"
					bind:start={form.servicePeriodStartOn}
					bind:end={form.servicePeriodEndOn}
					bind:invalid={servicePeriodInvalid}
					presets
					presetMode="service-period"
				/>
				{#if errors.servicePeriod}<span class="block text-xs font-normal text-destructive">{errors.servicePeriod}</span>{/if}
			</div>
			<label class="space-y-1 text-sm font-medium sm:col-span-2" for="one-time-charge-description">
				<span>Description</span>
				<Input id="one-time-charge-description" bind:value={form.description} placeholder="January water bill" />
				{#if errors.description}<span class="block text-xs font-normal text-destructive">{errors.description}</span>{/if}
			</label>
			{#if form.chargeType === 'Other'}
				<div class="space-y-1 text-sm font-medium sm:col-span-2" data-testid="one-time-charge-category">
					<span>Income category <span class="font-normal text-muted-foreground">(required for Other)</span></span>
					<AccountPicker bind:selectedAccountId={form.categoryAccountId} allowAll={false} types={['Income']} />
					<p class="text-xs font-normal text-muted-foreground">Choose where this charge should appear in income reports.</p>
					{#if errors.category}<span class="block text-xs font-normal text-destructive">{errors.category}</span>{/if}
				</div>
			{:else if accountsQuery.isLoading}
				<p class="text-xs text-muted-foreground sm:col-span-2">The matching income category is loading.</p>
			{:else if resolvedAccount}
				<p class="text-xs text-muted-foreground sm:col-span-2">This charge uses the matching income category automatically.</p>
			{/if}
		</div>

		<label class="mt-4 flex cursor-pointer items-center gap-2 rounded-lg border border-dashed border-border px-3 py-2 text-sm" for="one-time-charge-document">
			<span class="font-medium">Document</span>
			<span class="min-w-0 truncate text-muted-foreground">{documentName || 'Attach…'}</span>
			<input id="one-time-charge-document" class="sr-only" type="file" accept="image/*,.pdf" onchange={selectDocument} />
		</label>

		{#if errors.category && form.chargeType !== 'Other'}<p class="mt-3 text-sm text-destructive" role="alert">{errors.category}</p>{/if}

		<Dialog.Footer>
			<Button variant="outline" onclick={close} disabled={mutation.isPending}>Cancel</Button>
			<Button onclick={() => validate() && mutation.mutate()} disabled={datePickerSubmitDisabled(datePickerInvalid, mutation.isPending)} data-testid="one-time-charge-submit">
				{mutation.isPending ? 'Adding…' : 'Add charge'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
