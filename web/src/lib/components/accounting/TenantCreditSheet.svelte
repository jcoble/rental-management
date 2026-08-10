<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import { ApiError } from '$lib/api/client';
	import { tenantLedgers, type TenantCreditTarget } from '$lib/api/endpoints/tenant-ledgers';
	import { tenantMoney } from '$lib/api/endpoints/tenant-money';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { formatAccountingCurrency, formatAccountingDate } from '$lib/accounting/accounting-display';
	import { normalizeTenantLedgerDescription } from '$lib/accounting/tenant-ledger-display';
	import { projectedRemainingCharge } from '$lib/accounting/tenant-credit-preview';
	import AccountPicker from './AccountPicker.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import { ACCOUNTING_HELP } from '$lib/accounting/accounting-help';

	const TARGETED_CREDIT_ERROR = "This credit is larger than what's left of the original charge. Enter it as a standalone credit instead.";

	let {
		open,
		tenantAccountId,
		currency = 'USD',
		initialTargetEntryId = null,
		onclose,
		onsaved
	}: {
		open: boolean;
		tenantAccountId: number;
		currency?: string;
		initialTargetEntryId?: number | null;
		onclose: () => void;
		onsaved: () => void;
	} = $props();

	const today = () => new Date().toISOString().slice(0, 10);
	let form = $state({
		amount: '',
		effectiveOn: today(),
		reason: '',
		applyToCharge: true,
		targetChargeEntryId: '',
		categoryAccountId: null as number | null
	});
	let errors = $state<Record<string, string>>({});
	let documentName = $state('');
	let initializedKey = $state('');
	let operationKey = $state<string | null>(null);
	let targetSkip = $state(0);

	const targetQuery = createQuery(() => ({
		queryKey: ['tenant-ledger-credit-targets', tenantAccountId, targetSkip, initialTargetEntryId],
		enabled: open && tenantAccountId > 0,
		queryFn: () => tenantLedgers.creditTargets(tenantAccountId, {
			skip: targetSkip,
			take: 50,
			targetEntryId: targetSkip === 0 ? initialTargetEntryId ?? undefined : undefined
		})
	}));
	const eligibleCharges = $derived(targetQuery.data?.items ?? []);
	const selectedTarget = $derived(
		eligibleCharges.find((row: TenantCreditTarget) => String(row.tenantLedgerEntryId) === form.targetChargeEntryId) ?? null
	);
	const projectedRemainingAmount = $derived(
		selectedTarget ? projectedRemainingCharge(selectedTarget.remainingTargetableAmount, form.amount) : 0
	);

	$effect(() => {
		const nextKey = open ? String(initialTargetEntryId ?? 'new') : '';
		if (open && nextKey !== initializedKey) {
			form = {
				amount: '',
				effectiveOn: today(),
				reason: '',
				applyToCharge: true,
				targetChargeEntryId: initialTargetEntryId ? String(initialTargetEntryId) : '',
				categoryAccountId: null
			};
			errors = {};
			documentName = '';
			operationKey = null;
			targetSkip = 0;
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
		if (!form.reason.trim()) errors.reason = 'Explain why this credit is being given.';
		if (form.applyToCharge && !form.targetChargeEntryId) errors.targetChargeEntryId = 'Choose the original charge or turn off the checkbox.';
		if (!form.applyToCharge && !form.categoryAccountId) errors.category = 'Choose an income category.';
		return Object.keys(errors).length === 0;
	}

	const mutation = createMutation(() => ({
		mutationFn: () => {
			operationKey ??= crypto.randomUUID();
			return tenantMoney.postCredit(tenantAccountId, operationKey, {
				amount: Number(form.amount),
				effectiveOn: form.effectiveOn,
				description: form.reason.trim(),
				targetChargeEntryId: form.applyToCharge ? Number(form.targetChargeEntryId) : null,
				incomeLedgerAccountId: form.applyToCharge ? null : form.categoryAccountId,
				allocateOldestCharges: false
			});
		},
		onSuccess: () => {
			showSuccess('Credit recorded.');
			onsaved();
			onclose();
		},
		onError: (error) => {
			if (form.applyToCharge && form.targetChargeEntryId && error instanceof ApiError && error.status === 400) {
				errors = { form: TARGETED_CREDIT_ERROR };
				return;
			}
			errors = { form: apiErrorMessage(error, 'The credit was not recorded.') };
		}
	}));
</script>

<Dialog.Root open={open} onOpenChange={(next) => { if (!next) close(); }}>
	<Dialog.Content class="max-w-xl" data-testid="tenant-credit-sheet">
		<Dialog.Header>
			<Dialog.Title>Give credit</Dialog.Title>
			<div class="flex items-start gap-1.5">
				<Dialog.Description>Credits keep the original charge and payment history intact.</Dialog.Description>
				<HelpPopover
					title={ACCOUNTING_HELP.tenantCredit.title}
					summary={ACCOUNTING_HELP.tenantCredit.summary}
					learnMoreUrl={ACCOUNTING_HELP.tenantCredit.href}
					testid="tenant-credit-help"
				/>
			</div>
		</Dialog.Header>

		<div class="grid gap-4 sm:grid-cols-2">
			<label class="space-y-1 text-sm font-medium" for="tenant-credit-amount">
				<span>Amount</span>
				<Input id="tenant-credit-amount" type="text" inputmode="decimal" mask="currency" bind:value={form.amount} placeholder="0.00" />
				{#if errors.amount}<span class="block text-xs font-normal text-destructive">{errors.amount}</span>{/if}
			</label>
			<label class="space-y-1 text-sm font-medium" for="tenant-credit-date">
				<span>Effective date</span>
				<DatePicker id="tenant-credit-date" bind:value={form.effectiveOn} />
				{#if errors.effectiveOn}<span class="block text-xs font-normal text-destructive">{errors.effectiveOn}</span>{/if}
			</label>
			<label class="space-y-1 text-sm font-medium sm:col-span-2" for="tenant-credit-reason">
				<span>Reason</span>
				<Input id="tenant-credit-reason" bind:value={form.reason} placeholder="Overcharged water bill" />
				{#if errors.reason}<span class="block text-xs font-normal text-destructive">{errors.reason}</span>{/if}
			</label>
		</div>

		<div class="mt-4 space-y-3 rounded-lg border border-border p-3">
			<label class="flex items-center gap-2 text-sm font-medium" for="tenant-credit-apply">
				<input id="tenant-credit-apply" type="checkbox" bind:checked={form.applyToCharge} />
				Apply to this charge
			</label>
			{#if form.applyToCharge}
				<div class="space-y-1 text-sm font-medium">
					<span>Original charge</span>
					<Select.Root type="single" bind:value={form.targetChargeEntryId}>
						<Select.Trigger class="w-full">{selectedTarget ? normalizeTenantLedgerDescription(selectedTarget.description) : 'Choose an original charge'}</Select.Trigger>
						<Select.Content>
							{#if targetQuery.isLoading}
								<div class="px-3 py-2 text-sm text-muted-foreground">Loading charges…</div>
							{:else if eligibleCharges.length === 0}
								<div class="px-3 py-2 text-sm text-muted-foreground">No eligible charges.</div>
							{:else}
								{#each eligibleCharges as charge (charge.tenantLedgerEntryId)}
									<Select.Item value={String(charge.tenantLedgerEntryId)} label={`${normalizeTenantLedgerDescription(charge.description)} · ${formatAccountingCurrency(charge.remainingTargetableAmount, currency)}`}>
										{normalizeTenantLedgerDescription(charge.description)} · {formatAccountingCurrency(charge.remainingTargetableAmount, currency)} · {formatAccountingDate(charge.effectiveOn)}
									</Select.Item>
								{/each}
							{/if}
						</Select.Content>
					</Select.Root>
					{#if targetQuery.data && targetQuery.data.totalCount > targetQuery.data.skip + targetQuery.data.take}
						<Button
							type="button"
							variant="ghost"
							size="sm"
							class="mt-1"
							onclick={() => targetSkip = targetQuery.data!.skip + targetQuery.data!.take}
							data-testid="tenant-credit-next-page"
						>
							Show more eligible charges
						</Button>
					{/if}
					{#if errors.targetChargeEntryId}<span class="block text-xs font-normal text-destructive">{errors.targetChargeEntryId}</span>{/if}
				</div>
			{:else}
				<div class="space-y-1 text-sm font-medium" data-testid="tenant-credit-category">
					<span>Category</span>
					<AccountPicker bind:selectedAccountId={form.categoryAccountId} allowAll={false} types={['Income']} />
					{#if errors.category}<span class="block text-xs font-normal text-destructive">{errors.category}</span>{/if}
				</div>
			{/if}
		</div>

		{#if selectedTarget && selectedTarget.remainingTargetableAmount > 0}
			<section class="mt-4 rounded-lg border border-border bg-muted/20 p-3" data-testid="tenant-credit-preview">
				<h3 class="text-sm font-semibold">Preview</h3>
				<dl class="mt-3 grid gap-2 text-sm sm:grid-cols-3">
					<div><dt class="text-xs text-muted-foreground">Original charge</dt><dd class="font-mono font-medium tabular-nums">{formatAccountingCurrency(selectedTarget.chargeAmount, currency)}</dd></div>
					<div><dt class="text-xs text-muted-foreground">Credit</dt><dd class="font-mono font-medium tabular-nums">{form.amount ? formatAccountingCurrency(Number(form.amount), currency) : '—'}</dd></div>
					<div><dt class="text-xs text-muted-foreground">Remaining charge</dt><dd class="font-mono font-medium tabular-nums">{formatAccountingCurrency(projectedRemainingAmount, currency)}</dd></div>
				</dl>
				<p class="mt-3 text-xs text-muted-foreground">The remaining balance is re-read from the server after the credit is saved.</p>
			</section>
		{:else if selectedTarget}
			<section class="mt-4 rounded-lg border border-border bg-muted/20 p-3" data-testid="tenant-credit-preview">
				<h3 class="text-sm font-semibold">Preview</h3>
				<dl class="mt-3 grid gap-2 text-sm sm:grid-cols-2">
					<div><dt class="text-xs text-muted-foreground">Credit</dt><dd class="font-mono font-medium tabular-nums">{form.amount ? formatAccountingCurrency(Number(form.amount), currency) : '—'}</dd></div>
					<div><dt class="text-xs text-muted-foreground">Tenant credit balance after save</dt><dd class="font-mono font-medium tabular-nums">{form.amount ? formatAccountingCurrency(Number(form.amount), currency) : '—'}</dd></div>
				</dl>
				<p class="mt-3 text-xs text-muted-foreground">This charge is already fully paid, so the server will keep the credit unapplied.</p>
			</section>
		{:else if !form.applyToCharge}
			<section class="mt-4 rounded-lg border border-border bg-muted/20 p-3" data-testid="tenant-credit-preview">
				<h3 class="text-sm font-semibold">Preview</h3>
				<p class="mt-2 text-sm text-muted-foreground">This will remain as an unapplied tenant credit until it is used.</p>
			</section>
		{/if}

		<label class="mt-4 flex cursor-pointer items-center gap-2 rounded-lg border border-dashed border-border px-3 py-2 text-sm" for="tenant-credit-document">
			<span class="font-medium">Document</span>
			<span class="min-w-0 truncate text-muted-foreground">{documentName || 'Attach…'}</span>
			<input id="tenant-credit-document" class="sr-only" type="file" accept="image/*,.pdf" onchange={selectDocument} />
		</label>

		{#if errors.form}<p class="mt-3 text-sm text-destructive" role="alert" data-testid="tenant-credit-error">{errors.form}</p>{/if}

		<Dialog.Footer>
			<Button variant="outline" onclick={close} disabled={mutation.isPending}>Cancel</Button>
			<Button onclick={() => validate() && mutation.mutate()} disabled={mutation.isPending} data-testid="tenant-credit-submit">
				{mutation.isPending ? 'Saving…' : 'Give credit'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
