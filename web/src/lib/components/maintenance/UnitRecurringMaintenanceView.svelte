<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { recurringMaintenance } from '$lib/api/endpoints/recurring-maintenance';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import RecurringMaintenanceFormDialog from '$lib/components/maintenance/RecurringMaintenanceFormDialog.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { Plus } from '@lucide/svelte';
	import { formatDateOnly } from '$lib/utils/date';

	type Props = {
		propertyId: number;
		propertyLabel: string;
		unitId: number;
		unitLabel: string;
		emptyMessage: string;
	};

	let {
		propertyId,
		propertyLabel,
		unitId,
		unitLabel,
		emptyMessage,
	}: Props = $props();

	const PAGE_SIZE = 10;
	let search = $state('');
	let sort = $state('nextDueDate');
	let skip = $state(0);
	let showForm = $state(false);

	const query = createQuery(() => ({
		queryKey: ['unit-recurring-maintenance', unitId, search, sort, skip, PAGE_SIZE],
		queryFn: () => recurringMaintenance.listPage({
			unitId,
			search: search || undefined,
			sort,
			skip,
			take: PAGE_SIZE,
		}),
		enabled: Number.isInteger(unitId) && unitId > 0,
	}));

	function openCreate() {
		showForm = true;
	}

	function closeForm() {
		showForm = false;
	}

	function handleSaved() {
		showForm = false;
		skip = 0;
		void query.refetch();
	}
</script>

<section tabindex="-1" class="scroll-mt-4 outline-none" data-testid="unit-recurring-section">
	<div class="space-y-3">
		<div><h2 class="text-lg font-semibold">Recurring work</h2><p class="text-sm text-muted-foreground">Maintenance that repeats on a schedule for this rental.</p></div>
		<div class="flex flex-wrap gap-2">
			<Input bind:value={search} oninput={() => skip = 0} placeholder="Search recurring work" aria-label="Search Unit recurring work" class="max-w-xs" />
			<Select.Root type="single" bind:value={sort} onValueChange={() => skip = 0}>
				<Select.Trigger class="w-full sm:w-52" aria-label="Sort Unit recurring work">
					<Select.Value placeholder="Next due" />
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="nextDueDate" label="Next due">Next due</Select.Item>
					<Select.Item value="-nextDueDate" label="Latest due">Latest due</Select.Item>
					<Select.Item value="title" label="Title">Title</Select.Item>
				</Select.Content>
			</Select.Root>
			<Button data-testid="unit-recurring-create-button" onclick={openCreate} class="gap-2">
				<Plus class="h-4 w-4" /> New recurring task
			</Button>
		</div>
		{#if query.isLoading}
			<LoadingState label="Loading recurring work" testid="unit-recurring-loading" />
		{:else if query.isError}
			<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="unit-recurring-error">
				<p class="text-sm font-medium text-destructive">Recurring work could not be loaded.</p>
				<Button class="mt-3" variant="outline" size="sm" onclick={() => query.refetch()}>Try again</Button>
			</div>
		{:else if !query.data?.items.length}<p class="text-sm text-muted-foreground" data-testid="unit-recurring-empty">{emptyMessage}</p>
		{:else}
			<ul class="divide-y rounded-lg border">
				{#each query.data.items as task (task.id)}
					<li class="flex items-center justify-between gap-3 p-3 text-sm" data-testid={task.testId}><span class="font-medium">{task.title}</span><span class="text-muted-foreground">{task.recurrenceInterval} · {formatDateOnly(task.nextDueDate)}</span></li>
				{/each}
			</ul>
		{/if}
		<div class="flex items-center justify-between">
			<Button variant="outline" size="sm" disabled={skip === 0} onclick={() => skip = Math.max(0, skip - PAGE_SIZE)}>Previous</Button>
			<span class="text-xs text-muted-foreground">{query.data?.totalCount ?? 0} total</span>
			<Button variant="outline" size="sm" disabled={!query.data || skip + PAGE_SIZE >= query.data.totalCount} onclick={() => skip += PAGE_SIZE}>Next</Button>
		</div>
	</div>
</section>

<RecurringMaintenanceFormDialog
	open={showForm}
	propertyId={propertyId}
	propertyLabel={propertyLabel}
	unitId={unitId}
	unitLabel={unitLabel}
	lockProperty
	lockUnit
	onclose={closeForm}
	onsaved={handleSaved}
/>
