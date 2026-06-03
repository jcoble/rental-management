<script lang="ts">
	import { createMutation as createSvelteMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { portal } from '$lib/api/endpoints/portal';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { Wrench } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const workOrdersQuery = createQuery(() => ({ queryKey: ['portal-work-orders'], queryFn: () => portal.workOrders() }));
	let form = $state({ title: '', description: '', category: 'Resident Request', priority: 'Normal' });

	const submitMutation = createSvelteMutation(() => ({
		mutationFn: () => portal.createTenantWorkOrder(form),
		onSuccess: () => {
			form = { title: '', description: '', category: 'Resident Request', priority: 'Normal' };
			queryClient.invalidateQueries({ queryKey: ['portal-work-orders'] });
			showSuccess('Maintenance request submitted.');
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function submit() {
		if (!form.title.trim() || !form.description.trim()) return;
		submitMutation.mutate();
	}
</script>

<svelte:head><title>Maintenance - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-5 flex items-center gap-2"><Wrench class="h-5 w-5 text-primary" /><h1 class="text-2xl font-semibold">Maintenance</h1></div>
	<div class="grid gap-5 lg:grid-cols-2">
		<section class="rounded-lg border border-border bg-card p-4">
			<h2 class="mb-3 font-semibold">Open Requests</h2>
			<div class="space-y-3">
				{#each (workOrdersQuery.data ?? []).filter((w) => !['Completed', 'Cancelled', 'Archived'].includes(String(w.status))) as order}
					<div class="rounded-md border border-border px-3 py-2">
						<p class="font-medium">{order.title}</p>
						<p class="text-sm text-muted-foreground">{order.status} · {order.priority}</p>
					</div>
				{:else}
					<p class="text-sm text-muted-foreground">No open requests.</p>
				{/each}
			</div>
		</section>
		<section class="rounded-lg border border-border bg-card p-4">
			<h2 class="mb-3 font-semibold">Submit Request</h2>
			<form class="space-y-3" onsubmit={(e) => { e.preventDefault(); submit(); }}>
				<Input bind:value={form.title} placeholder="Issue title" />
				<textarea bind:value={form.description} rows={5} class="w-full rounded-md border border-border bg-background px-3 py-2 text-sm" placeholder="Describe the issue"></textarea>
				<div class="grid gap-3 sm:grid-cols-2">
					<Input bind:value={form.category} placeholder="Category" />
					<Select.Root type="single" bind:value={form.priority}>
						<Select.Trigger class="w-full">{form.priority}</Select.Trigger>
						<Select.Content>
							<Select.Item value="Low" label="Low">Low</Select.Item>
							<Select.Item value="Normal" label="Normal">Normal</Select.Item>
							<Select.Item value="High" label="High">High</Select.Item>
							<Select.Item value="Emergency" label="Emergency">Emergency</Select.Item>
						</Select.Content>
					</Select.Root>
				</div>
				<Button type="submit" disabled={submitMutation.isPending}>{submitMutation.isPending ? 'Submitting...' : 'Submit Request'}</Button>
			</form>
		</section>
	</div>
</div>
