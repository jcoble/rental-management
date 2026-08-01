<!--
  LeaseTermFields — the single source-of-truth field group for a Lease's TERM fields (number, dates,
  rent, deposit, late fee, due day, status, and notes). Consumed by BOTH the manual New-Lease modal
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
	import { addCalendarYear } from '$lib/utils/parse-date';
	import { formatStatusLabel } from '$lib/utils/status-labels';

	let {
		form = $bindable(),
		errors = {},
		autoFilled,
		confidence,
		statuses = ['Draft', 'Active', 'Expired', 'Terminated'],
		section = 'all',
		showLifecycleFields = true,
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
			status: string;
			notes: string;
		};
		errors?: Record<string, string>;
		autoFilled?: Set<string>;
		confidence?: Record<string, number>;
		statuses?: readonly string[];
		section?: 'all' | 'identity' | 'dates' | 'money' | 'rent' | 'fees' | 'status';
		showLifecycleFields?: boolean;
		testidPrefix?: string;
	} = $props();

	const filled = (key: string) => !!autoFilled?.has(key);
	const conf = (key: string) => confidence?.[STEP_FIELD_TO_EXTRACTION[key] ?? ''];
	const showIdentity = $derived(section === 'all' || section === 'identity');
	const showDates = $derived(section === 'all' || section === 'dates');
	const showMoney = $derived(section === 'all' || section === 'money');
	const showRent = $derived(showMoney || section === 'rent');
	const showFees = $derived(showMoney || section === 'fees');
	const showStatus = $derived(showLifecycleFields && (section === 'all' || section === 'status'));
	let autoDefaultedEndDate = $state('');

	function handleStartDateChange(iso: string) {
		form.startDate = iso;
		if (!iso) return;
		const defaultEndDate = addCalendarYear(iso);
		if (!defaultEndDate) return;
		if (!form.endDate || form.endDate === autoDefaultedEndDate) {
			form.endDate = defaultEndDate;
			autoDefaultedEndDate = defaultEndDate;
		}
	}

	function handleEndDateChange(iso: string) {
		form.endDate = iso;
		if (iso !== autoDefaultedEndDate) autoDefaultedEndDate = '';
	}
</script>

<div class="grid gap-3 md:grid-cols-2" data-testid={`${testidPrefix}-term-fields`}>
	{#if showIdentity}
	<div class="md:col-span-2">
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Lease number</span>
			<AutoFilledBadge show={filled('leaseNumber')} confidence={conf('leaseNumber')} />
		</div>
		<Input data-testid={`${testidPrefix}-number-input`} bind:value={form.leaseNumber} placeholder="Lease number" />
		{#if errors.leaseNumber}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-number-error`}>{errors.leaseNumber}</p>{/if}
	</div>
	{/if}
	{#if showDates}
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Start date</span>
			<AutoFilledBadge show={filled('startDate')} confidence={conf('startDate')} />
		</div>
		<DatePicker
			testid={`${testidPrefix}-start-input`}
			bind:value={form.startDate}
			onchange={handleStartDateChange}
			placeholder="Start date"
			max={form.endDate || undefined}
		/>
		{#if errors.startDate}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-start-error`}>{errors.startDate}</p>{/if}
	</div>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">End date</span>
			<AutoFilledBadge show={filled('endDate')} confidence={conf('endDate')} />
		</div>
		<DatePicker
			testid={`${testidPrefix}-end-input`}
			bind:value={form.endDate}
			onchange={handleEndDateChange}
			placeholder="End date"
			min={form.startDate || undefined}
		/>
		{#if errors.endDate}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-end-error`}>{errors.endDate}</p>{/if}
	</div>
	{/if}
	{#if showRent}
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Monthly rent</span>
			<AutoFilledBadge show={filled('monthlyRent')} confidence={conf('monthlyRent')} />
		</div>
		<Input data-testid={`${testidPrefix}-rent-input`} bind:value={form.monthlyRent} placeholder="Monthly rent" inputmode="decimal" mask="currency" />
		{#if errors.monthlyRent}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-rent-error`}>{errors.monthlyRent}</p>{/if}
	</div>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Security deposit</span>
			<AutoFilledBadge show={filled('securityDeposit')} confidence={conf('securityDeposit')} />
		</div>
		<Input data-testid={`${testidPrefix}-deposit-input`} bind:value={form.securityDeposit} placeholder="Security deposit" inputmode="decimal" mask="currency" />
		{#if errors.securityDeposit}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-deposit-error`}>{errors.securityDeposit}</p>{/if}
	</div>
	{/if}
	{#if showFees}
	<div class="grid grid-cols-2 gap-2">
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">Late fee</span>
				<AutoFilledBadge show={filled('lateFeeAmount')} confidence={conf('lateFeeAmount')} />
			</div>
			<Input data-testid={`${testidPrefix}-late-fee-input`} bind:value={form.lateFeeAmount} placeholder="Late fee" inputmode="decimal" mask="currency" />
			{#if errors.lateFeeAmount}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-late-fee-error`}>{errors.lateFeeAmount}</p>{/if}
		</div>
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">Due day</span>
				<AutoFilledBadge show={filled('rentDueDay')} confidence={conf('rentDueDay')} />
			</div>
			<Input data-testid={`${testidPrefix}-due-day-input`} bind:value={form.rentDueDay} placeholder="Due day" inputmode="numeric" maxlength={2} mask="integer" />
			{#if errors.rentDueDay}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-due-day-error`}>{errors.rentDueDay}</p>{/if}
		</div>
	</div>
	{/if}
	{#if showStatus}
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
	<div class="md:col-span-2">
		<span class="mb-1 block text-xs font-medium text-muted-foreground">Notes</span>
		<textarea
			data-testid={`${testidPrefix}-notes-input`}
			bind:value={form.notes}
			rows="3"
			placeholder="Optional lease notes"
			class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
		></textarea>
		{#if errors.notes}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-notes-error`}>{errors.notes}</p>{/if}
	</div>
	{/if}
</div>
