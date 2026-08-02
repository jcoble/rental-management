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
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';

	export interface OneTimeChargeSeed {
		description?: string;
		effectiveOn?: string;
		dueOn?: string;
		chargeType?: TenantChargeType;
	}

	let {
		open,
		tenantAccountId,
		seed = null,
		onclose,
		onsaved
	}: {
		open: boolean;
		tenantAccountId: number;
		seed?: OneTimeChargeSeed | null;
		onclose: () => void;
		onsaved: () => void;
	} = $props();

	const today = () => new Date().toISOString().slice(0, 10);
	let form = $state({
		chargeType: 'Other' as TenantChargeType,
		amount: '',
		effectiveOn: today(),
		dueOn: today(),
		servicePeriodStartOn: '',
		servicePeriodEndOn: '',
		description: '',
		categoryAccountId: null as number | null
	});
	let errors = $state<Record<string, string>>({});
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
	const resolvedAccount = $derived.by(() => {
		if (chargeMapping.systemKey) return accounts.find((account) => account.systemKey === chargeMapping.systemKey) ?? null;
		return accounts.find((account) => account.id === form.categoryAccountId) ?? null;
	});

	$effect(() => {
		const nextKey = open
			? `${seed?.description ?? ''}|${seed?.effectiveOn ?? ''}|${seed?.dueOn ?? ''}|${seed?.chargeType ?? ''}`
			: '';
		if (open && nextKey !== initializedKey) {
			form = {
				chargeType: seed?.chargeType ?? 'Other',
				amount: '',
				effectiveOn: seed?.effectiveOn ?? today(),
				dueOn: seed?.dueOn ?? seed?.effectiveOn ?? today(),
				servicePeriodStartOn: '',
				servicePeriodEndOn: '',
				description: seed?.description ?? '',
				categoryAccountId: null
			};
			errors = {};
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
		if (!form.effectiveOn) errors.effectiveOn = 'Pick the effective date.';
		if (!form.dueOn) errors.dueOn = 'Pick the due date.';
		if (!form.description.trim()) errors.description = 'Describe this charge.';
		if ((form.servicePeriodStartOn && !form.servicePeriodEndOn) || (!form.servicePeriodStartOn && form.servicePeriodEndOn)) {
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
	<Dialog.Content class="max-w-xl" data-testid="one-time-charge-sheet">
		<Dialog.Header>
			<Dialog.Title>Add one-time charge</Dialog.Title>
			<Dialog.Description>This creates a real rent or resident charge for this tenant account.</Dialog.Description>
		</Dialog.Header>

		<div class="grid gap-4 sm:grid-cols-2">
			<label class="space-y-1 text-sm font-medium sm:col-span-2" for="one-time-charge-type">
				<span>What is this charge for?</span>
				<Select.Root type="single" bind:value={form.chargeType}>
					<Select.Trigger id="one-time-charge-type" class="w-full">{chargeMapping.label}</Select.Trigger>
					<Select.Content>{#each TENANT_CHARGE_TYPES as option}<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>{/each}</Select.Content>
				</Select.Root>
			</label>
			<label class="space-y-1 text-sm font-medium" for="one-time-charge-amount">
				<span>Amount</span>
				<Input id="one-time-charge-amount" type="text" inputmode="decimal" mask="currency" bind:value={form.amount} placeholder="0.00" />
				{#if errors.amount}<span class="block text-xs font-normal text-destructive">{errors.amount}</span>{/if}
			</label>
			<label class="space-y-1 text-sm font-medium" for="one-time-charge-effective-date">
				<span>Effective date</span>
				<DatePicker id="one-time-charge-effective-date" bind:value={form.effectiveOn} />
				{#if errors.effectiveOn}<span class="block text-xs font-normal text-destructive">{errors.effectiveOn}</span>{/if}
			</label>
			<label class="space-y-1 text-sm font-medium" for="one-time-charge-due-date">
				<span>Due date</span>
				<DatePicker id="one-time-charge-due-date" bind:value={form.dueOn} />
				{#if errors.dueOn}<span class="block text-xs font-normal text-destructive">{errors.dueOn}</span>{/if}
			</label>
			<div class="space-y-1 text-sm font-medium">
				<span>Service period <span class="font-normal text-muted-foreground">(optional)</span></span>
				<div class="grid gap-2 sm:grid-cols-2">
					<label class="space-y-1 text-xs font-normal text-muted-foreground" for="one-time-charge-service-start">
						<span>Start</span>
						<DatePicker id="one-time-charge-service-start" bind:value={form.servicePeriodStartOn} />
					</label>
					<label class="space-y-1 text-xs font-normal text-muted-foreground" for="one-time-charge-service-end">
						<span>End</span>
						<DatePicker id="one-time-charge-service-end" bind:value={form.servicePeriodEndOn} />
					</label>
				</div>
				{#if errors.servicePeriod}<span class="block text-xs font-normal text-destructive">{errors.servicePeriod}</span>{/if}
			</div>
			<label class="space-y-1 text-sm font-medium sm:col-span-2" for="one-time-charge-description">
				<span>Description</span>
				<Input id="one-time-charge-description" bind:value={form.description} placeholder="January water bill" />
				{#if errors.description}<span class="block text-xs font-normal text-destructive">{errors.description}</span>{/if}
			</label>
			{#if form.chargeType === 'Other'}
				<div class="space-y-1 text-sm font-medium sm:col-span-2" data-testid="one-time-charge-category">
					<span>Category</span>
					<AccountPicker bind:selectedAccountId={form.categoryAccountId} allowAll={false} types={['Income']} />
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
			<Button onclick={() => validate() && mutation.mutate()} disabled={mutation.isPending} data-testid="one-time-charge-submit">
				{mutation.isPending ? 'Adding…' : 'Add charge'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
