<script lang="ts">
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { appointmentSchema } from '$lib/schemas';
	import { clearFieldErrorWhen } from '$lib/forms/form-errors';

	type AppointmentForm = {
		title: string;
		type: string;
		status: string;
		[key: string]: string;
	};

	const APPT_TYPES = ['Showing', 'MoveIn', 'MoveOut', 'Inspection', 'MaintenanceVisit', 'OwnerMeeting'];
	const APPT_STATUSES = ['Scheduled', 'Confirmed', 'Completed', 'Cancelled', 'NoShow'];
	const typeLabels: Record<string, string> = {
		Showing: 'Showing',
		MoveIn: 'Move-in',
		MoveOut: 'Move-out',
		Inspection: 'Inspection',
		MaintenanceVisit: 'Maintenance visit',
		OwnerMeeting: 'Owner meeting',
	};
	const labelForType = (value: string) => typeLabels[value] ?? value;

	let {
		form = $bindable(),
		errors = $bindable({}),
		testidPrefix = 'appointment'
	}: {
		form: AppointmentForm;
		errors?: Record<string, string>;
		testidPrefix?: string;
	} = $props();

	$effect(() => {
		const valid = appointmentSchema.shape.title.safeParse(form.title).success;
		const next = clearFieldErrorWhen(errors, 'title', valid);
		if (next !== errors) errors = next;
	});
</script>

<div class="space-y-4" data-testid={`${testidPrefix}-details-fields`}>
	<div>
		<label class="mb-2 block text-sm font-medium text-muted-foreground" for={`${testidPrefix}-title`}>Appointment title</label>
		<Input id={`${testidPrefix}-title`} data-testid={`${testidPrefix}-title-input`} bind:value={form.title} placeholder="Appointment title" maxlength={200} />
		{#if errors.title}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-title-error`}>{errors.title}</p>{/if}
	</div>
	<div class="grid gap-4 sm:grid-cols-2" data-testid={`${testidPrefix}-type-status-fields`}>
		<div>
			<span class="mb-2 block text-sm font-medium text-muted-foreground">Type</span>
			<Select.Root type="single" bind:value={form.type}>
				<Select.Trigger class="w-full" data-testid={`${testidPrefix}-type-input`}>
					{form.type ? labelForType(form.type) : 'Select type'}
				</Select.Trigger>
				<Select.Content>
					{#each APPT_TYPES as type}<Select.Item value={type} label={labelForType(type)}>{labelForType(type)}</Select.Item>{/each}
				</Select.Content>
			</Select.Root>
		</div>
		<div>
			<span class="mb-2 block text-sm font-medium text-muted-foreground">Status</span>
			<Select.Root type="single" bind:value={form.status}>
				<Select.Trigger class="w-full" data-testid={`${testidPrefix}-status-input`}>
					{form.status || 'Select status'}
				</Select.Trigger>
				<Select.Content>
					{#each APPT_STATUSES as status}<Select.Item value={status} label={status}>{status}</Select.Item>{/each}
				</Select.Content>
			</Select.Root>
		</div>
	</div>
</div>
