<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import { tenantLedgers, type TenantLedgerRow } from '$lib/api/endpoints/tenant-ledgers';
	import { tenantMoney } from '$lib/api/endpoints/tenant-money';
	import { PAYMENT_METHODS } from '$lib/constants/payments';
	import { formatAccountingCurrency, formatAccountingDate } from '$lib/accounting/accounting-display';
	import { normalizeTenantLedgerDescription } from '$lib/accounting/tenant-ledger-display';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { businessDateOrToday } from '$lib/utils/business-date';

	let {
		open,
		tenantAccountId,
		currency = 'USD',
		defaultPayerName = '',
		businessDate = null,
		onclose,
		onsaved
	}: {
		open: boolean;
		tenantAccountId: number;
		currency?: string;
		defaultPayerName?: string;
		businessDate?: string | null;
		onclose: () => void;
		onsaved: () => void;
	} = $props();

	const moneyDate = $derived(businessDateOrToday(businessDate));
	let form = $state({
		amount: '',
		effectiveOn: businessDateOrToday(undefined),
		method: '',
		reference: '',
		payerName: '',
		note: 'Tenant payment',
		applyMode: 'oldest' as 'oldest' | 'specific',
		targetChargeEntryId: ''
	});
	let errors = $state<Record<string, string>>({});
	let documentName = $state('');
	let initializedKey = $state('');
	let operationKey = $state<string | null>(null);

	const openChargesQuery = createQuery(() => ({
		queryKey: ['tenant-ledger-open-charges', tenantAccountId],
		enabled: open && tenantAccountId > 0,
		queryFn: () => tenantLedgers.list(tenantAccountId, {
			openOnly: true,
			take: 100,
			sort: 'effectiveOn'
		})
	}));
	const openCharges = $derived(openChargesQuery.data?.items ?? []);

	$effect(() => {
		const nextKey = open ? `open|${defaultPayerName}` : '';
		if (open && nextKey !== initializedKey) {
			form = {
				amount: '',
				effectiveOn: moneyDate,
				method: '',
				reference: '',
				payerName: defaultPayerName,
				note: 'Tenant payment',
				applyMode: 'oldest',
				targetChargeEntryId: ''
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
		if (!form.effectiveOn) errors.effectiveOn = 'Pick the date received.';
		if (!form.method) errors.method = 'Choose a payment method.';
		if (form.applyMode === 'specific' && !form.targetChargeEntryId) {
			errors.targetChargeEntryId = 'Choose an open charge.';
		}
		return Object.keys(errors).length === 0;
	}

	const mutation = createMutation(() => ({
		mutationFn: () => {
			operationKey ??= crypto.randomUUID();
			return tenantMoney.recordReceipt(tenantAccountId, operationKey, {
				amount: Number(form.amount),
				effectiveOn: form.effectiveOn,
				description: form.note.trim() || 'Tenant payment',
				paymentMethodSummary: form.method,
				externalReference: form.reference.trim() || null,
				payerName: form.payerName.trim() || null,
				targetChargeEntryId: form.applyMode === 'specific' ? Number(form.targetChargeEntryId) : null,
				allocateOldestCharges: form.applyMode === 'oldest'
			});
		},
		onSuccess: () => {
			showSuccess('Payment recorded.');
			onsaved();
			onclose();
		},
		onError: (error) => showError(apiErrorMessage(error, 'The payment was not recorded.'))
	}));
</script>

<Dialog.Root open={open} onOpenChange={(next) => { if (!next) close(); }}>
	<Dialog.Content class="max-w-xl" data-testid="record-payment-sheet">
		<Dialog.Header>
			<Dialog.Title>Record payment</Dialog.Title>
			<Dialog.Description>Save the receipt without changing any posted history.</Dialog.Description>
		</Dialog.Header>

		<div class="grid gap-4 sm:grid-cols-2">
			<label class="space-y-1 text-sm font-medium" for="record-payment-amount">
				<span>Amount</span>
				<Input id="record-payment-amount" type="text" inputmode="decimal" mask="currency" bind:value={form.amount} aria-invalid={!!errors.amount} placeholder="0.00" />
				{#if errors.amount}<span class="block text-xs font-normal text-destructive">{errors.amount}</span>{/if}
			</label>
			<label class="space-y-1 text-sm font-medium" for="record-payment-date">
				<span>Date received</span>
				<DatePicker id="record-payment-date" testid="record-payment-date" bind:value={form.effectiveOn} todayValue={moneyDate} />
				{#if errors.effectiveOn}<span class="block text-xs font-normal text-destructive">{errors.effectiveOn}</span>{/if}
			</label>
			<label class="space-y-1 text-sm font-medium" for="record-payment-method">
				<span>Payment method</span>
				<Select.Root type="single" bind:value={form.method}>
					<Select.Trigger id="record-payment-method" class="w-full">{form.method || 'Choose a method'}</Select.Trigger>
					<Select.Content>
						{#each PAYMENT_METHODS as method}<Select.Item value={method} label={method}>{method}</Select.Item>{/each}
					</Select.Content>
				</Select.Root>
				{#if errors.method}<span class="block text-xs font-normal text-destructive">{errors.method}</span>{/if}
			</label>
			<label class="space-y-1 text-sm font-medium" for="record-payment-reference">
				<span>Reference / check #</span>
				<Input id="record-payment-reference" bind:value={form.reference} placeholder="Optional" />
			</label>
			<label class="space-y-1 text-sm font-medium" for="record-payment-payer">
				<span>Payer</span>
				<Input id="record-payment-payer" bind:value={form.payerName} placeholder="Tenant name" />
			</label>
			<label class="space-y-1 text-sm font-medium sm:col-span-2" for="record-payment-note">
				<span>Note</span>
				<Input id="record-payment-note" bind:value={form.note} placeholder="Optional note" />
			</label>
		</div>

		<fieldset class="space-y-3 rounded-lg border border-border p-3" data-testid="record-payment-allocation">
			<legend class="px-1 text-sm font-medium">Apply to</legend>
			<label class="flex items-center gap-2 text-sm">
				<input type="radio" name="record-payment-apply" value="oldest" bind:group={form.applyMode} />
				Oldest open charges first
			</label>
			<label class="flex items-start gap-2 text-sm">
				<input class="mt-1" type="radio" name="record-payment-apply" value="specific" bind:group={form.applyMode} />
				<span class="min-w-0 flex-1">
					<span class="block">A specific charge</span>
					<Select.Root type="single" bind:value={form.targetChargeEntryId} disabled={form.applyMode !== 'specific'}>
						<Select.Trigger class="mt-2 w-full">{#if form.targetChargeEntryId}{normalizeTenantLedgerDescription(openCharges.find((charge) => String(charge.tenantLedgerEntryId) === form.targetChargeEntryId)?.description) || 'Selected charge'}{:else}Choose an open charge{/if}</Select.Trigger>
						<Select.Content>
							{#if openChargesQuery.isLoading}
								<div class="px-3 py-2 text-sm text-muted-foreground">Loading open charges…</div>
							{:else if openCharges.length === 0}
								<div class="px-3 py-2 text-sm text-muted-foreground">No open charges.</div>
							{:else}
								{#each openCharges as charge (charge.tenantLedgerEntryId)}
									<Select.Item value={String(charge.tenantLedgerEntryId)} label={`${normalizeTenantLedgerDescription(charge.description)} · ${formatAccountingCurrency(charge.openAmount, currency)}`}>
										{normalizeTenantLedgerDescription(charge.description)} · {formatAccountingCurrency(charge.openAmount, currency)} open · {formatAccountingDate(charge.effectiveOn)}
									</Select.Item>
								{/each}
							{/if}
						</Select.Content>
					</Select.Root>
					{#if errors.targetChargeEntryId}<span class="mt-1 block text-xs font-normal text-destructive">{errors.targetChargeEntryId}</span>{/if}
				</span>
			</label>
		</fieldset>

		<label class="flex cursor-pointer items-center gap-2 rounded-lg border border-dashed border-border px-3 py-2 text-sm" for="record-payment-document">
			<span class="font-medium">Receipt document</span>
			<span class="min-w-0 truncate text-muted-foreground">{documentName || 'Attach…'}</span>
			<input id="record-payment-document" class="sr-only" type="file" accept="image/*,.pdf" onchange={selectDocument} />
		</label>

		<Dialog.Footer>
			<Button variant="outline" onclick={close} disabled={mutation.isPending}>Cancel</Button>
			<Button onclick={() => validate() && mutation.mutate()} disabled={mutation.isPending} data-testid="record-payment-submit">
				{mutation.isPending ? 'Recording…' : 'Record payment'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
