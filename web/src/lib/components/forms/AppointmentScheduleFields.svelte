<script lang="ts">
	import DateTimePicker from '$lib/components/shared/DateTimePicker.svelte';
	import { appointmentSchema, parseForm } from '$lib/schemas';
	import { clearFieldErrorWhen } from '$lib/forms/form-errors';

	type AppointmentScheduleForm = {
		scheduledStart: string;
		scheduledEnd: string;
		[key: string]: string;
	};

	let {
		form = $bindable(),
		errors = $bindable({}),
		testidPrefix = 'appointment'
	}: {
		form: AppointmentScheduleForm;
		errors?: Record<string, string>;
		testidPrefix?: string;
	} = $props();

	function clearScheduleError(field: 'scheduledStart' | 'scheduledEnd') {
		const shouldClear =
			field === 'scheduledStart'
				? appointmentSchema.shape.scheduledStart.safeParse(form.scheduledStart).success
				: !form.scheduledEnd || !parseForm(appointmentSchema, form).errors?.scheduledEnd;
		const next = clearFieldErrorWhen(errors, field, shouldClear);
		if (next !== errors) errors = next;
	}

	$effect(() => clearScheduleError('scheduledStart'));
	$effect(() => clearScheduleError('scheduledEnd'));
</script>

<div class="grid gap-4" data-testid={`${testidPrefix}-schedule-fields`}>
	<div>
		<span class="mb-2 block text-sm font-medium text-muted-foreground">Start</span>
		<DateTimePicker bind:value={form.scheduledStart} testid={`${testidPrefix}-start-input`} />
		{#if errors.scheduledStart}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-start-error`}>{errors.scheduledStart}</p>{/if}
	</div>
	<div>
		<span class="mb-2 block text-sm font-medium text-muted-foreground">End</span>
		<DateTimePicker bind:value={form.scheduledEnd} testid={`${testidPrefix}-end-input`} />
		{#if errors.scheduledEnd}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-end-error`}>{errors.scheduledEnd}</p>{/if}
	</div>
</div>
