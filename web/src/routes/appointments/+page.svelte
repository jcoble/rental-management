<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { appointments } from '$lib/api/endpoints/appointments';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { Plus } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const appointmentsQuery = createQuery(() => ({
		queryKey: ['appointments', portfolioId],
		queryFn: () => appointments.list(portfolioId),
	}));
	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId),
	}));
	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId),
	}));

	let showCreate = $state(false);
	let form = $state({
		title: '',
		type: 'Showing',
		scheduledStart: '',
		scheduledEnd: '',
		propertyId: '',
		tenantId: '',
		prospectName: '',
		prospectEmail: '',
		assignedTo: '',
		status: 'Scheduled',
	});

	const createAppointmentMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => appointments.create(data),
		onSuccess: () => {
			showCreate = false;
			form = { title: '', type: 'Showing', scheduledStart: '', scheduledEnd: '', propertyId: '', tenantId: '', prospectName: '', prospectEmail: '', assignedTo: '', status: 'Scheduled' };
			queryClient.invalidateQueries({ queryKey: ['appointments', portfolioId] });
		},
	}));

	const statusMutation = createMutation(() => ({
		mutationFn: ({ id, status }: { id: number; status: string }) => appointments.update(id, { status }),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: ['appointments', portfolioId] }),
	}));

	function submit() {
		if (!form.title || !form.scheduledStart) return;
		createAppointmentMutation.mutate({
			portfolioId,
			title: form.title,
			type: form.type,
			status: form.status,
			scheduledStart: form.scheduledStart,
			scheduledEnd: form.scheduledEnd || null,
			propertyId: form.propertyId ? Number(form.propertyId) : null,
			tenantId: form.tenantId ? Number(form.tenantId) : null,
			prospectName: form.prospectName || null,
			prospectEmail: form.prospectEmail || null,
			assignedTo: form.assignedTo || null,
		});
	}
</script>

<svelte:head>
	<title>Appointments - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-4 flex items-center justify-between">
		<div>
			<h1 class="text-2xl font-bold">Appointments</h1>
			<p class="text-sm text-text-secondary">Showings, move-ins, inspections, and service visits.</p>
		</div>
		<button class="inline-flex items-center gap-2 rounded-md bg-accent px-3 py-2 text-sm text-white" onclick={() => (showCreate = !showCreate)}>
			<Plus class="h-4 w-4" />
			New Appointment
		</button>
	</div>

	{#if showCreate}
		<div class="mb-5 rounded-lg border border-border bg-surface p-4">
			<div class="grid gap-3 md:grid-cols-3">
				<input bind:value={form.title} class="rounded border border-border bg-bg px-3 py-2 text-sm md:col-span-2" placeholder="Appointment title" />
				<select bind:value={form.type} class="rounded border border-border bg-bg px-3 py-2 text-sm"><option>Showing</option><option>MoveIn</option><option>MoveOut</option><option>Inspection</option><option>MaintenanceVisit</option><option>OwnerMeeting</option></select>
				<input type="datetime-local" bind:value={form.scheduledStart} class="rounded border border-border bg-bg px-3 py-2 text-sm" />
				<input type="datetime-local" bind:value={form.scheduledEnd} class="rounded border border-border bg-bg px-3 py-2 text-sm" />
				<select bind:value={form.status} class="rounded border border-border bg-bg px-3 py-2 text-sm"><option>Scheduled</option><option>Confirmed</option><option>Completed</option><option>Cancelled</option><option>NoShow</option></select>
				<select bind:value={form.propertyId} class="rounded border border-border bg-bg px-3 py-2 text-sm"><option value="">No property</option>{#each propertiesQuery.data || [] as property}<option value={property.id}>{property.name}</option>{/each}</select>
				<select bind:value={form.tenantId} class="rounded border border-border bg-bg px-3 py-2 text-sm"><option value="">No tenant</option>{#each tenantsQuery.data || [] as tenant}<option value={tenant.id}>{tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}</option>{/each}</select>
				<input bind:value={form.assignedTo} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Assigned to" />
				<input bind:value={form.prospectName} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Prospect name" />
				<input bind:value={form.prospectEmail} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Prospect email" />
			</div>
			<div class="mt-3"><button onclick={submit} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={createAppointmentMutation.isPending}>Save Appointment</button></div>
		</div>
	{/if}

	<div class="grid gap-3">
		{#each appointmentsQuery.data || [] as appointment}
			<div class="rounded-lg border border-border bg-surface p-4">
				<div class="flex items-center justify-between gap-2">
					<div>
						<p class="font-medium">{appointment.title}</p>
						<p class="text-xs text-text-secondary">{new Date(appointment.scheduledStart).toLocaleString()} · {appointment.type}</p>
					</div>
					<div class="flex items-center gap-2">
						<span class="rounded border border-border bg-bg px-2 py-0.5 text-xs">{appointment.status}</span>
						{#if appointment.status !== 'Completed'}
							<button class="rounded border border-border px-2 py-1 text-xs" onclick={() => statusMutation.mutate({ id: appointment.id, status: 'Completed' })}>Complete</button>
						{/if}
					</div>
				</div>
				<p class="mt-1 text-xs text-text-secondary">{appointment.propertyName || 'No property'} · {appointment.tenantName || appointment.prospectName || 'No contact'}</p>
			</div>
		{/each}
	</div>
</div>
