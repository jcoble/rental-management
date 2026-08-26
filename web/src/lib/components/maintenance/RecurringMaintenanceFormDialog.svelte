<script lang="ts">
	import { createMutation } from '@tanstack/svelte-query';
	import {
		recurringMaintenance,
		type RecurringMaintenanceTask,
	} from '$lib/api/endpoints/recurring-maintenance';
	import { properties } from '$lib/api/endpoints/properties';
	import { units } from '$lib/api/endpoints/units';
	import { vendors } from '$lib/api/endpoints/vendors';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { recurringMaintenanceSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import RemoteRecordSelect from '$lib/components/shared/RemoteRecordSelect.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import { clearFieldError } from '$lib/forms/form-errors';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';

	type Props = {
		open: boolean;
		task?: RecurringMaintenanceTask | null;
		tasks?: RecurringMaintenanceTask[];
		propertyId?: number | null;
		propertyLabel?: string;
		unitId?: number | null;
		unitLabel?: string;
		lockProperty?: boolean;
		lockUnit?: boolean;
		onclose: () => void;
		onsaved: (task: RecurringMaintenanceTask) => void;
	};

	let {
		open,
		task = null,
		tasks = [],
		propertyId = null,
		propertyLabel = '',
		unitId = null,
		unitLabel = '',
		lockProperty = false,
		lockUnit = false,
		onclose,
		onsaved
	}: Props = $props();

	const portfolioId = $derived(getCurrentPortfolioId());
	const INTERVALS = ['Weekly', 'Monthly', 'Quarterly', 'SemiAnnually', 'Annually'] as const;
	const PRIORITIES = ['Low', 'Normal', 'High', 'Emergency'] as const;
	const INTERVAL_PHRASE: Record<string, string> = {
		Weekly: 'Runs every Week',
		Monthly: 'Runs every Month',
		Quarterly: 'Runs every Quarter',
		SemiAnnually: 'Runs every 6 Months',
		Annually: 'Runs every Year',
	};
	const emptyForm = {
		propertyId: '',
		title: '',
		description: '',
		category: '',
		unitId: '',
		vendorId: '',
		recurrenceInterval: 'Monthly',
		nextDueDate: '',
		scheduledTime: '',
		estimatedCost: '',
		priority: 'Normal',
		isActive: true,
	};

	let form = $state({ ...emptyForm });
	let selectedPropertyLabel = $state('');
	let selectedUnitLabel = $state('');
	let selectedVendorLabel = $state('');
	let errors = $state<Record<string, string>>({});
	let initializedKey = $state('');
	const editingId = $derived(task?.id ?? null);
	const linkedTask = $derived(
		task ? (tasks.find((candidate) => candidate.id === task?.id) ?? task) : null
	);

	$effect(() => {
		const nextKey = open
			? `${task?.id ?? 'new'}|${propertyId ?? ''}|${unitId ?? ''}`
			: '';
		if (open && nextKey !== initializedKey) {
			form = task
				? {
						propertyId: String(task.propertyId),
						title: task.title,
						description: task.description ?? '',
						category: task.category ?? '',
						unitId: task.unitId == null ? '' : String(task.unitId),
						vendorId: task.vendorId == null ? '' : String(task.vendorId),
						recurrenceInterval: task.recurrenceInterval,
						nextDueDate: task.nextDueDate ? task.nextDueDate.slice(0, 10) : '',
						scheduledTime: task.scheduledTime ? task.scheduledTime.slice(0, 5) : '',
						estimatedCost: task.estimatedCost == null ? '' : String(task.estimatedCost),
						priority: task.priority,
						isActive: task.isActive,
					}
				: {
						...emptyForm,
						propertyId: propertyId == null ? '' : String(propertyId),
						unitId: unitId == null ? '' : String(unitId),
					};
			selectedPropertyLabel = task?.propertyName ?? (propertyLabel || (task ? `Property #${task.propertyId}` : ''));
			selectedUnitLabel = task?.unitNumber ? `Unit ${task.unitNumber}` : unitLabel;
			selectedVendorLabel = task?.vendorName ?? '';
			errors = {};
			initializedKey = nextKey;
		}
		if (!open) initializedKey = '';
	});

	function clearRecurringError(field: string) {
		const next = clearFieldError(errors, field);
		if (next !== errors) errors = next;
	}

	$effect(() => {
		if (form.propertyId) clearRecurringError('propertyId');
	});
	$effect(() => {
		if (form.title.trim()) clearRecurringError('title');
	});
	$effect(() => {
		if (form.nextDueDate) clearRecurringError('nextDueDate');
	});
	$effect(() => {
		if (form.category.trim()) clearRecurringError('category');
	});

	async function loadPropertyOptions(params: { search?: string; skip: number; take: number }) {
		const result = await properties.listPage(portfolioId, {
			...params,
			search: params.search ?? '',
			sort: 'name'
		});
		return {
			...result,
			items: result.items.map((property) => ({ id: property.id, label: property.name })),
		};
	}

	async function loadUnitOptions(params: { search?: string; skip: number; take: number }) {
		if (!form.propertyId) return { items: [], totalCount: 0, skip: params.skip, take: params.take };
		const result = await units.listWithHealthPage({
			...params,
			search: params.search ?? '',
			propertyId: Number(form.propertyId),
			sort: 'unitNumber',
		});
		return {
			...result,
			items: result.items.map((unit) => ({
				id: unit.id,
				label: `Unit ${unit.unitNumber}`,
			})),
		};
	}

	async function loadVendorOptions(params: { search?: string; skip: number; take: number }) {
		const result = await vendors.listPage(portfolioId, {
			...params,
			search: params.search ?? '',
			sort: 'name'
		});
		return {
			...result,
			items: result.items.map((vendor) => ({ id: vendor.id, label: vendor.name })),
		};
	}

	const saveMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number | null; data: Record<string, unknown> }) =>
			id == null
				? recurringMaintenance.create(data as never)
				: recurringMaintenance.update(id, data as never),
		onSuccess: (saved, vars) => {
			showSuccess(vars.id == null ? 'Recurring task created.' : 'Recurring task updated.');
			onsaved(saved);
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// Reset the unit selection when the property changes (units are property-scoped).
	function onPropertyChange(value: string) {
		if (value !== form.propertyId) {
			form.unitId = '';
			selectedUnitLabel = '';
		}
		form.propertyId = value;
	}

	function submit() {
		const result = parseForm(recurringMaintenanceSchema, form);
		if (result.errors) {
			errors = result.errors;
			return;
		}
		errors = {};
		const d = result.data;
		// On create, propertyId is part of the payload; on update it is omitted
		// (the API does not allow re-pointing it).
		const base = {
			title: d.title,
			description: d.description,
			category: d.category,
			unitId: d.unitId,
			vendorId: d.vendorId,
			recurrenceInterval: d.recurrenceInterval,
			nextDueDate: d.nextDueDate,
			scheduledTime: d.scheduledTime ? `${d.scheduledTime}:00` : null,
			estimatedCost: d.estimatedCost,
			priority: d.priority,
			isActive: d.isActive,
		};
		const payload = editingId == null ? { propertyId: d.propertyId, ...base } : base;
		saveMutation.mutate({ id: editingId, data: payload });
	}
</script>

<Dialog.Root open={open} onOpenChange={(next) => { if (!next) onclose(); }}>
	<Dialog.Content class="max-w-lg" data-testid="recurring-task-dialog">
		<Dialog.Header data-testid="recurring-task-dialog-header">
			<Dialog.Title data-testid="recurring-task-dialog-title">{editingId == null ? 'New Recurring Task' : 'Edit Recurring Task'}</Dialog.Title>
		</Dialog.Header>
		<div class="space-y-3" data-testid="recurring-task-form">
			<div data-testid="recurring-task-title-field">
				<label class="mb-1 block text-sm font-medium" for="rt-title">What needs doing?</label>
				<Input id="rt-title" data-testid="recurring-task-title-input" bind:value={form.title} placeholder="e.g. Change HVAC filter" />
				{#if errors.title}<p class="mt-1 text-xs text-destructive" data-testid="recurring-task-title-error">{errors.title}</p>{/if}
			</div>

			<div data-testid="recurring-task-property-field">
				<RemoteRecordSelect
					queryKey={['recurring-task-properties', portfolioId]}
					label="Property"
					bind:value={form.propertyId}
					selectedLabel={selectedPropertyLabel}
					placeholder="Select property"
					searchPlaceholder="Search properties…"
					disabled={editingId != null || lockProperty}
					required
					testid="recurring-task-property-input"
					loadPage={loadPropertyOptions}
					onValueChange={(value, option) => {
						onPropertyChange(value);
						selectedPropertyLabel = option?.label ?? '';
					}}
				/>
				{#if editingId != null}<p class="mt-1 text-xs text-muted-foreground" data-testid="recurring-task-property-locked">The property can't be changed after creation.</p>{/if}
				{#if errors.propertyId}<p class="mt-1 text-xs text-destructive" data-testid="recurring-task-property-error">{errors.propertyId}</p>{/if}
			</div>

			<div data-testid="recurring-task-unit-field">
				<RemoteRecordSelect
					queryKey={['recurring-task-units', portfolioId, form.propertyId]}
					label="Unit (optional)"
					bind:value={form.unitId}
					selectedLabel={selectedUnitLabel}
					placeholder={form.propertyId ? 'Whole property' : 'Pick a property first'}
					searchPlaceholder="Search units…"
					clearLabel="Whole property"
					disabled={!form.propertyId || lockUnit}
					testid="recurring-task-unit-input"
					loadPage={loadUnitOptions}
					onValueChange={(_value, option) => (selectedUnitLabel = option?.label ?? '')}
				/>
			</div>

			<div data-testid="recurring-task-vendor-field">
				<RemoteRecordSelect
					queryKey={['recurring-task-vendors', portfolioId]}
					label="Vendor (optional)"
					bind:value={form.vendorId}
					selectedLabel={selectedVendorLabel}
					placeholder="No vendor"
					searchPlaceholder="Search vendors…"
					clearLabel="No vendor"
					testid="recurring-task-vendor-input"
					loadPage={loadVendorOptions}
					onValueChange={(_value, option) => (selectedVendorLabel = option?.label ?? '')}
				/>
			</div>

			<div class="grid grid-cols-2 gap-2" data-testid="recurring-task-schedule-fields">
				<div data-testid="recurring-task-interval-field">
					<span class="mb-1 block text-sm font-medium">How often?</span>
					<Select.Root type="single" bind:value={form.recurrenceInterval}>
						<Select.Trigger class="w-full" data-testid="recurring-task-interval-input">
							{INTERVAL_PHRASE[form.recurrenceInterval] ?? form.recurrenceInterval}
						</Select.Trigger>
						<Select.Content>
							{#each INTERVALS as interval}
								<Select.Item value={interval} label={INTERVAL_PHRASE[interval]}>{INTERVAL_PHRASE[interval]}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
				<div data-testid="recurring-task-next-due-field">
					<label class="mb-1 block text-sm font-medium" for="rt-next">Next due</label>
					<DatePicker id="rt-next" testid="recurring-task-next-due-input" bind:value={form.nextDueDate} placeholder="Next due date" />
					{#if errors.nextDueDate}<p class="mt-1 text-xs text-destructive" data-testid="recurring-task-next-due-error">{errors.nextDueDate}</p>{/if}
				</div>
			</div>

			<div class="grid grid-cols-2 gap-2" data-testid="recurring-task-budget-fields">
				<div data-testid="recurring-task-scheduled-time-field">
					<label class="mb-1 block text-sm font-medium" for="rt-time">Scheduled time <span class="text-muted-foreground">(optional)</span></label>
					<Input id="rt-time" data-testid="recurring-task-scheduled-time-input" type="time" bind:value={form.scheduledTime} />
				</div>
				<div data-testid="recurring-task-estimated-cost-field">
					<label class="mb-1 block text-sm font-medium" for="rt-estimated-cost">Expected cost <span class="text-muted-foreground">(optional)</span></label>
					<Input id="rt-estimated-cost" data-testid="recurring-task-estimated-cost-input" type="text" inputmode="decimal" mask="currency" bind:value={form.estimatedCost} placeholder="0.00" />
					{#if errors.estimatedCost}<p class="mt-1 text-xs text-destructive" data-testid="recurring-task-estimated-cost-error">{errors.estimatedCost}</p>{/if}
				</div>
			</div>

			{#if editingId != null}
				<div class="rounded-md border border-border bg-muted/30 px-3 py-2 text-sm" data-testid="recurring-task-linked-summary">
					{#if (linkedTask?.generatedWorkOrderCount ?? 0) > 0}
						{linkedTask?.generatedWorkOrderCount} generated repair{(linkedTask?.generatedWorkOrderCount ?? 0) === 1 ? '' : 's'} linked to this schedule.
					{:else}
						No repairs have been generated from this schedule yet.
					{/if}
				</div>
			{/if}

			<div class="grid grid-cols-2 gap-2" data-testid="recurring-task-classification-fields">
				<div data-testid="recurring-task-priority-field">
					<span class="mb-1 block text-sm font-medium">Priority</span>
					<Select.Root type="single" bind:value={form.priority}>
						<Select.Trigger class="w-full" data-testid="recurring-task-priority-input">
							{form.priority ? formatStatusLabel(form.priority) : 'Priority'}
						</Select.Trigger>
						<Select.Content>
							{#each PRIORITIES as priority}
								<Select.Item value={priority} label={formatStatusLabel(priority)}>{formatStatusLabel(priority)}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
				<div data-testid="recurring-task-category-field">
					<label class="mb-1 block text-sm font-medium" for="rt-category">Category <span class="text-muted-foreground">(optional)</span></label>
					<Input id="rt-category" data-testid="recurring-task-category-input" bind:value={form.category} placeholder="e.g. HVAC" />
				</div>
			</div>

			<div data-testid="recurring-task-description-field">
				<label class="mb-1 block text-sm font-medium" for="rt-desc">Notes <span class="text-muted-foreground">(optional)</span></label>
				<textarea
					id="rt-desc"
					data-testid="recurring-task-description-input"
					bind:value={form.description}
					rows={2}
					class="w-full rounded border border-border bg-background px-3 py-2 text-sm"
					placeholder="Anything the vendor or you should know"
				></textarea>
			</div>

			<label class="flex items-center gap-2 text-sm" data-testid="recurring-task-active-input">
				<Checkbox bind:checked={form.isActive} />
				<span>Active <span class="text-muted-foreground">— when on, we create the repair automatically each time it's due.</span></span>
			</label>
		</div>
		<Dialog.Footer data-testid="recurring-task-dialog-footer">
			<Button variant="outline" data-testid="recurring-task-form-cancel" onclick={onclose}>Cancel</Button>
			<Button data-testid="recurring-task-form-save" onclick={submit} disabled={saveMutation.isPending}>
				{saveMutation.isPending ? 'Saving…' : 'Save'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
