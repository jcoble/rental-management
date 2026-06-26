<!--
  LeaseTermFields — the single source-of-truth field group for a Lease's TERM fields (number, dates,
  rent, deposit, late fee, due day, status, notes). Consumed by BOTH the manual New-Lease modal
  (leases/+page.svelte — which keeps its own property/unit/tenant pickers above this) AND the guided
  scan flow's Step 4 (property/unit/tenant resolved in prior steps). Owns ONLY the term field rows +
  inline errors + the optional "from your lease" badge (AC-3) — not the dialog chrome, the pickers,
  or the submit button.
-->
<script lang="ts">
	import { Input } from '$lib/components/ui/input';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import * as Select from '$lib/components/ui/select';
	import AutoFilledBadge from './AutoFilledBadge.svelte';
	import { STEP_FIELD_TO_EXTRACTION } from '$lib/scan/lease-prefill';
	import { formatStatusLabel } from '$lib/utils/status-labels';

	const rentTrackingStartOptions = [
		{ value: 'ForwardOnly', label: 'Start from today' },
		{ value: 'BackfillFromLeaseStart', label: 'Backfill from lease start' },
		{ value: 'CustomCutoffDate', label: 'Use cutoff date' }
	] as const;

	function rentTrackingStartLabel(value: string | undefined) {
		return rentTrackingStartOptions.find((option) => option.value === value)?.label ?? 'Select start';
	}

	let {
		form = $bindable(),
		errors = {},
		autoFilled,
		confidence,
		statuses = ['Draft', 'Active', 'Expired', 'Terminated'],
		testidPrefix = 'lease'
	}: {
		form: {
			leaseNumber: string;
			startDate: string;
			endDate: string;
			monthlyRent: string;
			securityDeposit: string;
			lateFeeAmount: string;
			rentDueDay: string;
			rentTrackingStartMode?: string;
			rentTrackingStartDate?: string;
			status: string;
			notes: string;
		};
		errors?: Record<string, string>;
		autoFilled?: Set<string>;
		confidence?: Record<string, number>;
		statuses?: readonly string[];
		testidPrefix?: string;
	} = $props();

	const filled = (key: string) => !!autoFilled?.has(key);
	const conf = (key: string) => confidence?.[STEP_FIELD_TO_EXTRACTION[key] ?? ''];
</script>

<div class="grid gap-3 md:grid-cols-2" data-testid={`${testidPrefix}-term-fields`}>
	<div class="md:col-span-2">
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Lease number</span>
			<AutoFilledBadge show={filled('leaseNumber')} confidence={conf('leaseNumber')} />
		</div>
		<Input data-testid={`${testidPrefix}-number-input`} bind:value={form.leaseNumber} placeholder="Lease number" />
		{#if errors.leaseNumber}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-number-error`}>{errors.leaseNumber}</p>{/if}
	</div>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Start date</span>
			<AutoFilledBadge show={filled('startDate')} confidence={conf('startDate')} />
		</div>
		<DatePicker testid={`${testidPrefix}-start-input`} bind:value={form.startDate} placeholder="Start date" max={form.endDate || undefined} />
		{#if errors.startDate}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-start-error`}>{errors.startDate}</p>{/if}
	</div>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">End date</span>
			<AutoFilledBadge show={filled('endDate')} confidence={conf('endDate')} />
		</div>
		<DatePicker testid={`${testidPrefix}-end-input`} bind:value={form.endDate} placeholder="End date" min={form.startDate || undefined} />
		{#if errors.endDate}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-end-error`}>{errors.endDate}</p>{/if}
	</div>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Monthly rent</span>
			<AutoFilledBadge show={filled('monthlyRent')} confidence={conf('monthlyRent')} />
		</div>
		<Input data-testid={`${testidPrefix}-rent-input`} bind:value={form.monthlyRent} placeholder="Monthly rent" />
		{#if errors.monthlyRent}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-rent-error`}>{errors.monthlyRent}</p>{/if}
	</div>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Security deposit</span>
			<AutoFilledBadge show={filled('securityDeposit')} confidence={conf('securityDeposit')} />
		</div>
		<Input data-testid={`${testidPrefix}-deposit-input`} bind:value={form.securityDeposit} placeholder="Security deposit" />
		{#if errors.securityDeposit}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-deposit-error`}>{errors.securityDeposit}</p>{/if}
	</div>
	<div class="grid grid-cols-2 gap-2">
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">Late fee</span>
				<AutoFilledBadge show={filled('lateFeeAmount')} confidence={conf('lateFeeAmount')} />
			</div>
			<Input data-testid={`${testidPrefix}-late-fee-input`} bind:value={form.lateFeeAmount} placeholder="Late fee" />
			{#if errors.lateFeeAmount}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-late-fee-error`}>{errors.lateFeeAmount}</p>{/if}
		</div>
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">Due day</span>
				<AutoFilledBadge show={filled('rentDueDay')} confidence={conf('rentDueDay')} />
			</div>
			<Input data-testid={`${testidPrefix}-due-day-input`} bind:value={form.rentDueDay} placeholder="Due day" />
			{#if errors.rentDueDay}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-due-day-error`}>{errors.rentDueDay}</p>{/if}
		</div>
	</div>
	<div class="md:col-span-2">
		<span class="mb-1 block text-xs font-medium text-muted-foreground">Status</span>
		<Select.Root type="single" bind:value={form.status}>
			<Select.Trigger class="w-full" data-testid={`${testidPrefix}-status-input`}>{form.status ? formatStatusLabel(form.status) : 'Select status'}</Select.Trigger>
			<Select.Content>
				{#each statuses as s}
					<Select.Item value={s} label={formatStatusLabel(s)}>{formatStatusLabel(s)}</Select.Item>
				{/each}
			</Select.Content>
		</Select.Root>
	</div>
	{#if form.status === 'Active'}
		<div class="md:col-span-2 grid gap-3 md:grid-cols-2" data-testid={`${testidPrefix}-rent-tracking-fields`}>
			<div class={form.rentTrackingStartMode === 'CustomCutoffDate' ? '' : 'md:col-span-2'}>
				<span class="mb-1 block text-xs font-medium text-muted-foreground">Rent tracking start</span>
				<Select.Root type="single" bind:value={form.rentTrackingStartMode}>
					<Select.Trigger class="w-full" data-testid={`${testidPrefix}-rent-tracking-mode`}>
						{rentTrackingStartLabel(form.rentTrackingStartMode)}
					</Select.Trigger>
					<Select.Content>
						{#each rentTrackingStartOptions as option}
							<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			{#if form.rentTrackingStartMode === 'CustomCutoffDate'}
				<div>
					<span class="mb-1 block text-xs font-medium text-muted-foreground">Cutoff date</span>
					<DatePicker testid={`${testidPrefix}-rent-tracking-date`} bind:value={form.rentTrackingStartDate} placeholder="Cutoff date" min={form.startDate || undefined} />
					{#if errors.rentTrackingStartDate}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-rent-tracking-date-error`}>{errors.rentTrackingStartDate}</p>{/if}
				</div>
			{/if}
		</div>
	{/if}
</div>
