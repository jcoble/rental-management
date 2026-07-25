<script lang="ts">
	import { createMutation as createSvelteMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { portal } from '$lib/api/endpoints/portal';
	import { documents } from '$lib/api/endpoints/documents';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import * as Dialog from '$lib/components/ui/dialog';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import WorkOrderTimeline from '$lib/components/shared/WorkOrderTimeline.svelte';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';
	import { Camera, ChevronRight, Wrench, X } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const pageSize = 20;
	let searchInput = $state('');
	let search = $state('');
	let status = $state('All');
	let skip = $state(0);
	const workOrdersQuery = createQuery(() => ({
		queryKey: ['portal-work-orders', { search, status, skip }],
		queryFn: () => portal.workOrders({
			search: search || undefined,
			status: status === 'All' ? undefined : status,
			skip,
			take: pageSize,
			sort: '-requestedAt'
		})
	}));
	const workOrderPage = $derived(workOrdersQuery.data);
	const workOrders = $derived(workOrderPage?.items ?? []);
	const hasPreviousPage = $derived(skip > 0);
	const hasNextPage = $derived(skip + pageSize < (workOrderPage?.totalCount ?? 0));
	let form = $state({ title: '', description: '', category: 'Resident Request', priority: 'Normal' });
	let requestSubmitted = $state(false);
	const requestTitleError = $derived(requestSubmitted && !form.title.trim() ? 'Issue title is required.' : '');
	const requestDescriptionError = $derived(requestSubmitted && !form.description.trim() ? 'Describe the issue before submitting.' : '');

	// Photo attached to the new request, uploaded after the work order is created.
	let photoInput: HTMLInputElement | undefined = $state();
	let photoFile = $state<File | null>(null);
	let photoPreview = $state<string | null>(null);

	function onPhotoChange(e: Event) {
		const input = e.currentTarget as HTMLInputElement;
		const file = input.files?.[0] ?? null;
		if (photoPreview) URL.revokeObjectURL(photoPreview);
		photoFile = file;
		photoPreview = file ? URL.createObjectURL(file) : null;
	}

	function clearPhoto() {
		if (photoPreview) URL.revokeObjectURL(photoPreview);
		photoFile = null;
		photoPreview = null;
		if (photoInput) photoInput.value = '';
	}

	const submitMutation = createSvelteMutation(() => ({
		mutationFn: async () => {
			const wo = await portal.createTenantWorkOrder(form);
			// Best-effort photo upload; don't fail the whole request if only the photo fails.
			if (photoFile && wo?.id) {
				try {
					await documents.upload('WorkOrder', wo.id, photoFile, undefined, crypto.randomUUID());
				} catch (err) {
					showError(apiErrorMessage(err, 'Request saved, but the photo failed to upload.'));
				}
			}
			return wo;
		},
		onSuccess: () => {
			form = { title: '', description: '', category: 'Resident Request', priority: 'Normal' };
			requestSubmitted = false;
			clearPhoto();
			queryClient.invalidateQueries({ queryKey: ['portal-work-orders'] });
			showSuccess('Maintenance request submitted.');
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function submit() {
		requestSubmitted = true;
		const missingTitle = !form.title.trim();
		const missingDescription = !form.description.trim();
		if (missingTitle || missingDescription) return;
		submitMutation.mutate();
	}

	function applySearch() {
		search = searchInput.trim();
		skip = 0;
	}

	// --- Request detail / status timeline ---
	let openId = $state<number | null>(null);

	const detailQuery = createQuery(() => ({
		queryKey: ['portal-work-order', openId],
		queryFn: () => portal.workOrder(openId as number),
		enabled: openId != null
	}));

	const detail = $derived(detailQuery.data);

	function openDetail(id: number) {
		openId = id;
	}
	function closeDetail() {
		openId = null;
	}

	function isOpen(workOrderStatus: unknown): boolean {
		return !['Completed', 'Cancelled', 'Archived'].includes(String(workOrderStatus));
	}

</script>

<svelte:head><title>Maintenance - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-4 sm:p-6" data-testid="portal-maintenance-page">
	<div class="mb-5 flex items-center gap-2"><Wrench class="h-5 w-5 text-primary" /><h1 class="text-2xl font-semibold">Maintenance</h1></div>
	<div class="grid gap-5 lg:grid-cols-2">
		<section class="rounded-lg border border-border bg-card p-4">
			<h2 class="mb-3 font-semibold">Your requests</h2>
			<form class="mb-4 grid gap-2 sm:grid-cols-[1fr_11rem_auto]" onsubmit={(event) => { event.preventDefault(); applySearch(); }}>
				<Input bind:value={searchInput} placeholder="Search requests" aria-label="Search maintenance requests" />
				<Select.Root type="single" bind:value={status} onValueChange={() => { skip = 0; }}>
					<Select.Trigger class="w-full">{status === 'All' ? 'All statuses' : status}</Select.Trigger>
					<Select.Content>
						<Select.Item value="All" label="All statuses">All statuses</Select.Item>
						<Select.Item value="New" label="New">New</Select.Item>
						<Select.Item value="Scheduled" label="Scheduled">Scheduled</Select.Item>
						<Select.Item value="InProgress" label="In progress">In progress</Select.Item>
						<Select.Item value="WaitingParts" label="Waiting for parts">Waiting for parts</Select.Item>
						<Select.Item value="OnHold" label="On hold">On hold</Select.Item>
						<Select.Item value="Completed" label="Completed">Completed</Select.Item>
						<Select.Item value="Cancelled" label="Cancelled">Cancelled</Select.Item>
						<Select.Item value="Archived" label="Archived">Archived</Select.Item>
					</Select.Content>
				</Select.Root>
				<Button type="submit" variant="outline">Search</Button>
			</form>
			<div class="space-y-3">
				{#if workOrdersQuery.isLoading}
					<LoadingState label="Loading maintenance requests" testid="portal-work-orders-loading" />
				{:else if workOrdersQuery.isError}
					<div class="rounded-md border border-destructive/40 bg-destructive/5 p-3">
						<p class="text-sm font-medium text-destructive">Requests could not be loaded.</p>
						<Button type="button" variant="outline" size="sm" class="mt-2" onclick={() => workOrdersQuery.refetch()}>Try again</Button>
					</div>
				{:else}
				{#each workOrders as order (order.id)}
					<button
						type="button"
						class="flex w-full items-center gap-3 rounded-md border border-border px-3 py-2.5 text-left transition-colors hover:bg-accent/50"
						onclick={() => openDetail(order.id)}
						data-testid="portal-work-order-row"
					>
						<div class="min-w-0 flex-1">
							<p class="truncate font-medium">{order.title}</p>
							<p class="text-sm text-muted-foreground">{order.priority}</p>
						</div>
						<StatusBadge status={String(order.status)} />
						<ChevronRight class="h-4 w-4 shrink-0 text-muted-foreground" />
					</button>
				{:else}
					<p class="text-sm text-muted-foreground" data-testid="portal-work-orders-empty">No requests yet.</p>
				{/each}
				{/if}
			</div>
			{#if (workOrderPage?.totalCount ?? 0) > pageSize}
				<div class="mt-4 flex items-center justify-between border-t border-border pt-3 text-sm text-muted-foreground">
					<span>{skip + 1}–{Math.min(skip + pageSize, workOrderPage?.totalCount ?? 0)} of {workOrderPage?.totalCount ?? 0}</span>
					<div class="flex gap-2">
						<Button type="button" variant="outline" size="sm" disabled={!hasPreviousPage} onclick={() => { skip = Math.max(0, skip - pageSize); }}>Previous</Button>
						<Button type="button" variant="outline" size="sm" disabled={!hasNextPage} onclick={() => { skip += pageSize; }}>Next</Button>
					</div>
				</div>
			{/if}
		</section>

		<section class="rounded-lg border border-border bg-card p-4">
			<h2 class="mb-3 font-semibold">Submit a request</h2>
			<form class="space-y-3" onsubmit={(e) => { e.preventDefault(); submit(); }}>
				<div class="space-y-1">
					<Input
						bind:value={form.title}
						placeholder="What's the problem? (e.g. Kitchen sink leaking)"
						aria-required="true"
						aria-invalid={requestTitleError ? 'true' : undefined}
						aria-describedby={requestTitleError ? 'portal-request-title-error' : undefined}
						data-testid="portal-request-title"
					/>
					{#if requestTitleError}
						<p id="portal-request-title-error" class="text-xs font-medium text-destructive" data-testid="portal-request-title-error">
							{requestTitleError}
						</p>
					{/if}
				</div>
				<div class="space-y-1">
					<textarea
						bind:value={form.description}
						rows={5}
						class="w-full rounded-md border border-border bg-background px-3 py-2 text-sm aria-invalid:border-destructive aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40"
						placeholder="Describe the issue so we can fix it fast"
						aria-required="true"
						aria-invalid={requestDescriptionError ? 'true' : undefined}
						aria-describedby={requestDescriptionError ? 'portal-request-description-error' : undefined}
						data-testid="portal-request-description"
					></textarea>
					{#if requestDescriptionError}
						<p
							id="portal-request-description-error"
							class="text-xs font-medium text-destructive"
							data-testid="portal-request-description-error"
						>
							{requestDescriptionError}
						</p>
					{/if}
				</div>
				<div class="grid gap-3 sm:grid-cols-2">
					<Input bind:value={form.category} placeholder="Category" data-testid="portal-request-category" />
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

				<!-- Photo capture -->
				<div>
					{#if photoPreview}
						<div class="relative inline-block">
							<img src={photoPreview} alt="Attached" class="h-28 w-28 rounded-md border border-border object-cover" data-testid="portal-request-photo-preview" />
							<button
								type="button"
								class="absolute -right-2 -top-2 flex h-6 w-6 items-center justify-center rounded-full bg-foreground text-background shadow"
								onclick={clearPhoto}
								aria-label="Remove photo"
								data-testid="portal-request-photo-remove"
							>
								<X class="h-3.5 w-3.5" />
							</button>
						</div>
					{:else}
						<label
							class="inline-flex h-11 cursor-pointer items-center gap-2 rounded-md border border-dashed border-border bg-background px-4 text-sm font-medium text-muted-foreground transition-colors hover:bg-accent/50"
							data-testid="portal-request-photo-label"
						>
							<Camera class="h-4 w-4" />
							Add a photo (optional)
							<input
								bind:this={photoInput}
								type="file"
								accept="image/*"
								capture="environment"
								class="sr-only"
								onchange={onPhotoChange}
								data-testid="portal-request-photo-input"
							/>
						</label>
					{/if}
				</div>

				<Button type="submit" disabled={submitMutation.isPending} data-testid="portal-request-submit">
					{submitMutation.isPending ? 'Submitting...' : 'Submit Request'}
				</Button>
			</form>
		</section>
	</div>
</div>

<!-- Request detail + status timeline -->
<Dialog.Root open={openId != null} onOpenChange={(v) => { if (!v) closeDetail(); }}>
	<Dialog.Content class="max-h-[85vh] overflow-y-auto" data-testid="portal-work-order-detail">
		<Dialog.Header>
			<Dialog.Title>{detail?.title ?? 'Request'}</Dialog.Title>
		</Dialog.Header>

		{#if detailQuery.isLoading}
			<LoadingState label="Loading request details" variant="spinner" testid="portal-work-order-detail-loading" />
		{:else if detailQuery.isError || !detail}
			<div class="py-6 text-center" data-testid="portal-work-order-detail-error">
				<p class="text-sm font-medium text-destructive">Couldn't load this request.</p>
				{#if detailQuery.isError}
					<Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => detailQuery.refetch()}>Try again</Button>
				{/if}
			</div>
		{:else}
			<div class="space-y-4">
				<div class="flex flex-wrap items-center gap-2">
					<StatusBadge status={String(detail.status)} />
					<StatusBadge status={String(detail.priority)} />
					{#if isOpen(detail.status)}
						<span class="text-xs text-muted-foreground">We'll update this as work progresses.</span>
					{/if}
				</div>

				{#if detail.description}
					<p class="whitespace-pre-line text-sm text-foreground">{detail.description}</p>
				{/if}

				<DocumentsPanel title="Photos & documents" entityType="WorkOrder" entityId={detail.id} />

				<div>
					<h3 class="mb-2 text-sm font-semibold">Progress</h3>
					<WorkOrderTimeline timeline={detail.timeline} testid="portal-work-order-timeline" />
				</div>
			</div>
		{/if}

		<Dialog.Footer>
			<Button variant="outline" onclick={closeDetail} data-testid="portal-work-order-detail-close">Close</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
