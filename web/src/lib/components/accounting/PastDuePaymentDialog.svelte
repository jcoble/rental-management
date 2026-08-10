<script lang="ts">
	import type { PastDueLease } from '$lib/types';
	import type { TenantLedgerRow } from '$lib/api/endpoints/tenant-ledgers';
	import { PAYMENT_METHODS } from '$lib/constants/payments';
	import { formatAccountingCurrency, formatAccountingDate } from '$lib/accounting/accounting-display';
	import { normalizeTenantLedgerDescription } from '$lib/accounting/tenant-ledger-display';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';

	export type PastDueOpenCharge = Pick<
		TenantLedgerRow,
		'tenantLedgerEntryId' | 'description' | 'effectiveOn' | 'dueOn' | 'openAmount' | 'currency'
	>;

	export interface PastDuePaymentSubmission {
		amount: number;
		paidDate: string;
		method: string;
		externalReference?: string;
		notes?: string;
		allocateOldestCharges: true;
	}

	let {
		open,
		target,
		openCharges = [],
		totalOpenAmount,
		openChargesLoading = false,
		openChargesError = false,
		pending = false,
		onclose,
		onsubmit
	}: {
		open: boolean;
		target: PastDueLease | null;
		openCharges?: PastDueOpenCharge[];
		totalOpenAmount: number;
		openChargesLoading?: boolean;
		openChargesError?: boolean;
		pending?: boolean;
		onclose: () => void;
		onsubmit: (data: PastDuePaymentSubmission) => void;
	} = $props();

	const todayLocal = (): string => {
		const date = new Date();
		const pad = (value: number) => String(value).padStart(2, '0');
		return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
	};

	const loadLastMethod = (): string => {
		if (typeof localStorage === 'undefined') return '';
		try {
			return localStorage.getItem('rc.payments.lastMethod') ?? '';
		} catch {
			return '';
		}
	};

	const rememberLastMethod = (method: string): void => {
		if (!method || typeof localStorage === 'undefined') return;
		try {
			localStorage.setItem('rc.payments.lastMethod', method);
		} catch {
			// Storage is optional.
		}
	};

	let form = $state({ amount: '', paidDate: '', method: '', externalReference: '', notes: '' });
	let errors = $state<{ amount?: string; method?: string }>({});
	let initializedAccountId = $state<number | null>(null);

	$effect(() => {
		const accountId = target?.tenantAccountId ?? null;
		if (open && accountId !== null && initializedAccountId !== accountId) {
			form = {
				amount: totalOpenAmount.toFixed(2),
				paidDate: todayLocal(),
				method: loadLastMethod(),
				externalReference: '',
				notes: ''
			};
			errors = {};
			initializedAccountId = accountId;
		}
		if (!open) initializedAccountId = null;
	});

	const allocationPreview = $derived.by(() => {
		const amount = Number(form.amount);
		if (!Number.isFinite(amount) || amount <= 0) return [];
		let remaining = amount;
		const preview: Array<{ charge: PastDueOpenCharge; amount: number }> = [];
		for (const charge of openCharges) {
			if (remaining <= 0) break;
			const applied = Math.min(charge.openAmount, remaining);
			if (applied > 0) preview.push({ charge, amount: applied });
			remaining -= applied;
		}
		return preview;
	});

	function validate(): boolean {
		errors = {};
		const amount = Number(form.amount);
		if (!Number.isFinite(amount) || amount <= 0) {
			errors.amount = 'Enter an amount greater than $0.00.';
		} else if (amount > totalOpenAmount) {
			errors.amount = `Amount cannot exceed ${formatAccountingCurrency(totalOpenAmount)}.`;
		}
		if (!form.method) errors.method = 'Choose a payment method.';
		return !errors.amount && !errors.method;
	}

	function submit(): void {
		if (pending || openChargesLoading || openChargesError) return;
		if (!validate()) return;
		const amount = Number(form.amount);
		rememberLastMethod(form.method);
		onsubmit({
			amount,
			paidDate: form.paidDate || todayLocal(),
			method: form.method,
			externalReference: form.externalReference.trim() || undefined,
			notes: form.notes.trim() || undefined,
			allocateOldestCharges: true
		});
	}
</script>

<Dialog.Root open={open} onOpenChange={(next) => { if (!next && !pending) onclose(); }}>
	<Dialog.Content class="max-w-xl" data-testid="past-due-mark-paid-dialog">
		<Dialog.Header data-testid="past-due-mark-paid-header">
			<Dialog.Title data-testid="past-due-mark-paid-title">Record payment</Dialog.Title>
			{#if target}
				<Dialog.Description data-testid="past-due-mark-paid-summary">
					{target.tenantName || target.relationshipNumber || 'Tenant account'} · {formatAccountingCurrency(totalOpenAmount)} open
					{#if target.overduePaymentCount > 1} · {target.overduePaymentCount} charges{/if}
				</Dialog.Description>
			{/if}
		</Dialog.Header>

		<div class="space-y-3" data-testid="past-due-mark-paid-form">
			<label class="block space-y-1 text-sm font-medium" for="past-due-mark-paid-amount-input" data-testid="past-due-mark-paid-amount-field">
				<span data-testid="past-due-mark-paid-amount-label">Amount received</span>
				<Input
					id="past-due-mark-paid-amount-input"
					data-testid="past-due-mark-paid-amount-input"
					type="text"
					inputmode="decimal"
					bind:value={form.amount}
					aria-invalid={Boolean(errors.amount)}
					aria-describedby="past-due-mark-paid-amount-error"
				/>
				{#if errors.amount}<span class="block text-xs font-normal text-destructive" id="past-due-mark-paid-amount-error" data-testid="past-due-mark-paid-amount-error">{errors.amount}</span>{/if}
			</label>

			<div data-testid="past-due-allocation-preview">
				<div class="flex items-center justify-between gap-3">
					<p class="text-sm font-medium" data-testid="past-due-allocation-preview-label">Allocation preview</p>
					<span class="text-xs text-muted-foreground" data-testid="past-due-allocation-preview-order">Oldest open charges first</span>
				</div>
				{#if openChargesLoading}
					<p class="mt-2 text-xs text-muted-foreground" data-testid="past-due-allocation-preview-loading">Loading open charges…</p>
				{:else if openChargesError}
					<p class="mt-2 text-xs text-destructive" data-testid="past-due-allocation-preview-error">Couldn't load every open charge. Try again before recording this payment.</p>
				{:else if allocationPreview.length === 0}
					<p class="mt-2 text-xs text-muted-foreground" data-testid="past-due-allocation-preview-empty">Enter a positive amount to preview the charges it will cover.</p>
				{:else}
					<ul class="mt-2 space-y-1 rounded-md border border-border/70 bg-muted/20 px-3 py-2" data-testid="past-due-allocation-preview-list">
						{#each allocationPreview as allocation (allocation.charge.tenantLedgerEntryId)}
							<li class="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-0.5 text-xs" data-testid={`past-due-allocation-preview-row-${allocation.charge.tenantLedgerEntryId}`}>
								<span class="min-w-0 truncate" data-testid={`past-due-allocation-preview-charge-${allocation.charge.tenantLedgerEntryId}`}>
									{normalizeTenantLedgerDescription(allocation.charge.description) || 'Charge'} · {formatAccountingDate(allocation.charge.dueOn || allocation.charge.effectiveOn)}
								</span>
								<span class="shrink-0 font-mono tabular-nums" data-testid={`past-due-allocation-preview-amount-${allocation.charge.tenantLedgerEntryId}`}>
									{formatAccountingCurrency(allocation.amount, allocation.charge.currency)}
								</span>
							</li>
						{/each}
					</ul>
				{/if}
			</div>

			<label class="block space-y-1 text-sm font-medium" for="past-due-mark-paid-date-input" data-testid="past-due-mark-paid-date-field">
				<span data-testid="past-due-mark-paid-date-label">Date received</span>
				<DatePicker id="past-due-mark-paid-date-input" testid="past-due-mark-paid-date-input" bind:value={form.paidDate} placeholder="Date received" />
			</label>

			<label class="block space-y-1 text-sm font-medium" for="past-due-mark-paid-method-input" data-testid="past-due-mark-paid-method-field">
				<span data-testid="past-due-mark-paid-method-label">Method</span>
				<select id="past-due-mark-paid-method-input" data-testid="past-due-mark-paid-method-input" bind:value={form.method} class="w-full rounded-md border border-border bg-background px-3 py-2 text-sm">
					<option value="">Select method</option>
					{#each PAYMENT_METHODS as method}<option value={method}>{method}</option>{/each}
				</select>
				{#if errors.method}<span class="block text-xs font-normal text-destructive" data-testid="past-due-mark-paid-method-error">{errors.method}</span>{/if}
			</label>

			<label class="block space-y-1 text-sm font-medium" for="past-due-mark-paid-reference-input" data-testid="past-due-mark-paid-reference-field">
				<span data-testid="past-due-mark-paid-reference-label">Reference</span>
				<Input id="past-due-mark-paid-reference-input" data-testid="past-due-mark-paid-reference-input" bind:value={form.externalReference} placeholder="Check #, confirmation #, etc. (optional)" />
			</label>

			<label class="block space-y-1 text-sm font-medium" for="past-due-mark-paid-notes-input" data-testid="past-due-mark-paid-notes-field">
				<span data-testid="past-due-mark-paid-notes-label">Notes</span>
				<textarea id="past-due-mark-paid-notes-input" data-testid="past-due-mark-paid-notes-input" bind:value={form.notes} rows={2} maxlength={2000} placeholder="Anything to remember about this payment (optional)" class="w-full resize-none rounded-md border border-border bg-background px-3 py-2 text-sm"></textarea>
			</label>
		</div>

		<Dialog.Footer data-testid="past-due-mark-paid-footer">
			<Button data-testid="past-due-mark-paid-cancel" variant="outline" onclick={onclose} disabled={pending}>Cancel</Button>
			<Button data-testid="past-due-mark-paid-confirm" onclick={submit} disabled={pending || openChargesLoading || openChargesError}>
				{pending ? 'Recording…' : 'Record payment'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
