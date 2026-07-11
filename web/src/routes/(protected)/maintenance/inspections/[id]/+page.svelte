<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { inspections, openInspectionReport } from '$lib/api/endpoints/inspections';
	import { documents, fileObjectUrl } from '$lib/api/endpoints/documents';
	import type { InspectionCompleteResult, InspectionDetail, InspectionItem, InspectionItemInput, InspectionItemResult } from '$lib/types';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { recordHref } from '$lib/navigation/record-href';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { ArrowDown, ArrowUp, Camera, Check, ClipboardCheck, Download, Edit2, Minus, Plus, Save, Trash2, X } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const id = $derived(Number(page.params.id));

	const inspectionQuery = createQuery(() => ({
		queryKey: ['inspection', id],
		queryFn: () => inspections.get(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const inspection = $derived(inspectionQuery.data);
	const isCompleted = $derived(inspection?.status === 'Completed');
	const readOnly = $derived(isCompleted);
	const orderedItems = $derived.by(() =>
		[...(inspection?.items ?? [])].sort((a, b) => a.sortOrder - b.sortOrder || a.id - b.id)
	);

	// Group checklist items by area, preserving sort order within each group.
	const groupedItems = $derived.by(() => {
		const groups = new Map<string, InspectionItem[]>();
		for (const item of orderedItems) {
			const area = item.area || 'General';
			if (!groups.has(area)) groups.set(area, []);
			groups.get(area)!.push(item);
		}
		return [...groups.entries()].map(([area, areaItems]) => ({ area, items: areaItems }));
	});

	// Live progress counts for the header.
	const counts = $derived.by(() => {
		const items = inspection?.items ?? [];
		return {
			total: items.length,
			pass: items.filter((i) => i.result === 'Pass').length,
			fail: items.filter((i) => i.result === 'Fail').length,
			na: items.filter((i) => i.result === 'NotApplicable').length,
			pending: items.filter((i) => i.result === 'Pending').length,
		};
	});

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['inspection', id] });
		queryClient.invalidateQueries({ queryKey: ['inspections'] });
	}

	// Patch the item in the query cache so the UI updates without a full refetch.
	function sortItems(items: InspectionItem[]) {
		return [...items].sort((a, b) => a.sortOrder - b.sortOrder || a.id - b.id);
	}

	function setItemsInCache(items: InspectionItem[]) {
		queryClient.setQueryData<InspectionDetail>(['inspection', id], (prev) =>
			prev ? { ...prev, items: sortItems(items) } : prev
		);
	}

	function patchItemInCache(updated: InspectionItem) {
		queryClient.setQueryData<InspectionDetail>(['inspection', id], (prev) =>
			prev ? { ...prev, items: sortItems(prev.items.map((it) => (it.id === updated.id ? updated : it))) } : prev
		);
	}

	function addItemToCache(created: InspectionItem) {
		queryClient.setQueryData<InspectionDetail>(['inspection', id], (prev) =>
			prev ? { ...prev, items: sortItems([...prev.items, created]) } : prev
		);
	}

	function removeItemFromCache(itemId: number) {
		queryClient.setQueryData<InspectionDetail>(['inspection', id], (prev) =>
			prev ? { ...prev, items: prev.items.filter((it) => it.id !== itemId) } : prev
		);
	}

	// --- Checklist question structure (editable until completion) ---
	const emptyQuestionDraft = (): InspectionItemInput => ({ area: '', label: '' });
	let addQuestionDraft = $state<InspectionItemInput>(emptyQuestionDraft());
	let addQuestionErrors = $state<Record<string, string>>({});
	let editingQuestionId = $state<number | null>(null);
	let editQuestionDrafts = $state<Record<number, InspectionItemInput>>({});
	let editQuestionErrors = $state<Record<number, Record<string, string>>>({});
	let deletingQuestionId = $state<number | null>(null);
	let reorderingQuestionId = $state<number | null>(null);

	function validateQuestionDraft(draft: InspectionItemInput) {
		const area = draft.area.trim();
		const label = draft.label.trim();
		const errors: Record<string, string> = {};

		if (!area) errors.area = 'Area is required.';
		else if (area.length > 120) errors.area = 'Area must be 120 characters or fewer.';
		if (!label) errors.label = 'Question is required.';
		else if (label.length > 300) errors.label = 'Question must be 300 characters or fewer.';

		return Object.keys(errors).length > 0
			? { value: null, errors }
			: { value: { area, label } satisfies InspectionItemInput, errors };
	}

	const createQuestionMutation = createMutation(() => ({
		mutationFn: (data: InspectionItemInput) => inspections.createItem(id, data),
		onSuccess: (created) => {
			addItemToCache(created);
			addQuestionDraft = emptyQuestionDraft();
			addQuestionErrors = {};
			showSuccess('Question added.');
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not add question.')),
	}));

	function addQuestion() {
		if (readOnly) return;
		const result = validateQuestionDraft(addQuestionDraft);
		addQuestionErrors = result.errors;
		if (!result.value) return;
		createQuestionMutation.mutate(result.value);
	}

	function startEditingQuestion(item: InspectionItem) {
		if (readOnly) return;
		editingQuestionId = item.id;
		editQuestionDrafts = { ...editQuestionDrafts, [item.id]: { area: item.area, label: item.label } };
		editQuestionErrors = { ...editQuestionErrors, [item.id]: {} };
	}

	function cancelEditingQuestion(itemId: number) {
		editingQuestionId = null;
		const { [itemId]: _draft, ...remainingDrafts } = editQuestionDrafts;
		const { [itemId]: _errors, ...remainingErrors } = editQuestionErrors;
		editQuestionDrafts = remainingDrafts;
		editQuestionErrors = remainingErrors;
	}

	const updateQuestionMutation = createMutation(() => ({
		mutationFn: ({ itemId, data }: { itemId: number; data: InspectionItemInput }) =>
			inspections.updateItem(id, itemId, data),
		onSuccess: (updated) => {
			patchItemInCache(updated);
			cancelEditingQuestion(updated.id);
			showSuccess('Question updated.');
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not update question.')),
	}));

	function saveQuestion(item: InspectionItem) {
		if (readOnly) return;
		const draft = editQuestionDrafts[item.id] ?? { area: item.area, label: item.label };
		const result = validateQuestionDraft(draft);
		editQuestionErrors = { ...editQuestionErrors, [item.id]: result.errors };
		if (!result.value) return;
		if (result.value.area === item.area && result.value.label === item.label) {
			cancelEditingQuestion(item.id);
			return;
		}
		updateQuestionMutation.mutate({ itemId: item.id, data: result.value });
	}

	const deleteQuestionMutation = createMutation(() => ({
		mutationFn: (itemId: number) => inspections.deleteItem(id, itemId),
		onMutate: (itemId) => {
			deletingQuestionId = itemId;
		},
		onSuccess: (_result, itemId) => {
			removeItemFromCache(itemId);
			showSuccess('Question deleted.');
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not delete question.')),
		onSettled: () => {
			deletingQuestionId = null;
		},
	}));

	function deleteQuestion(item: InspectionItem) {
		if (readOnly) return;
		if (!window.confirm('Delete this checklist question?')) return;
		deleteQuestionMutation.mutate(item.id);
	}

	const reorderQuestionMutation = createMutation(() => ({
		mutationFn: ({ itemId, itemIds }: { itemId: number; itemIds: number[] }) =>
			inspections.reorderItems(id, itemIds).then((items) => ({ itemId, items })),
		onMutate: ({ itemId }) => {
			reorderingQuestionId = itemId;
		},
		onSuccess: ({ items }) => {
			setItemsInCache(items);
			showSuccess('Question order updated.');
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not reorder questions.')),
		onSettled: () => {
			reorderingQuestionId = null;
		},
	}));

	function moveQuestion(item: InspectionItem, direction: -1 | 1) {
		if (readOnly) return;
		const currentIndex = orderedItems.findIndex((it) => it.id === item.id);
		const nextIndex = currentIndex + direction;
		if (currentIndex < 0 || nextIndex < 0 || nextIndex >= orderedItems.length) return;
		const nextItems = [...orderedItems];
		[nextItems[currentIndex], nextItems[nextIndex]] = [nextItems[nextIndex], nextItems[currentIndex]];
		reorderQuestionMutation.mutate({ itemId: item.id, itemIds: nextItems.map((it) => it.id) });
	}

	// --- Result (Pass / Fail / N/A) ---
	let savingResultFor = $state<number | null>(null);

	const resultMutation = createMutation(() => ({
		mutationFn: ({ itemId, result }: { itemId: number; result: InspectionItemResult }) =>
			inspections.updateItem(id, itemId, { result }),
		onMutate: ({ itemId }) => {
			savingResultFor = itemId;
		},
		onSuccess: (updated) => {
			patchItemInCache(updated);
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not save.')),
		onSettled: () => {
			savingResultFor = null;
		},
	}));

	function setResult(item: InspectionItem, result: InspectionItemResult) {
		if (readOnly) return;
		if (item.result === result) return;
		resultMutation.mutate({ itemId: item.id, result });
	}

	// --- Note (saved on blur) ---
	// Local editable copy so typing doesn't fight the query cache; keyed by item id.
	let noteDrafts = $state<Record<number, string>>({});

	function noteValue(item: InspectionItem): string {
		return noteDrafts[item.id] ?? item.note ?? '';
	}

	const noteMutation = createMutation(() => ({
		mutationFn: ({ itemId, note }: { itemId: number; note: string }) =>
			inspections.updateItem(id, itemId, { note }),
		onSuccess: (updated) => {
			patchItemInCache(updated);
			showSuccess('Note saved.');
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not save note.')),
	}));

	function saveNote(item: InspectionItem) {
		if (readOnly) return;
		const draft = noteDrafts[item.id];
		if (draft === undefined) return;
		const next = draft.trim();
		if (next === (item.note ?? '')) return;
		noteMutation.mutate({ itemId: item.id, note: next });
	}

	// --- Photo capture (upload to documents, then attach storedFileId) ---
	let uploadingPhotoFor = $state<number | null>(null);
	const photoInputs: Record<number, HTMLInputElement | undefined> = {};
	// Object URLs for rendering thumbnails, keyed by storedFileId.
	let photoUrls = $state<Record<number, string>>({});

	function triggerPhoto(item: InspectionItem) {
		photoInputs[item.id]?.click();
	}

	async function handlePhoto(item: InspectionItem, e: Event) {
		const input = e.currentTarget as HTMLInputElement;
		const file = input.files?.[0];
		input.value = ''; // allow re-selecting the same file
		if (!file) return;
		uploadingPhotoFor = item.id;
		try {
			// 1. Upload the file as an Inspection document → returns a stored file (id).
			const doc = await documents.upload('Inspection', id, file, undefined, crypto.randomUUID());
			// 2. Attach that stored file to this checklist item.
			const updated = await inspections.setItemPhoto(id, item.id, doc.id);
			patchItemInCache(updated);
			showSuccess('Photo attached.');
		} catch (err) {
			showError(apiErrorMessage(err, 'Photo upload failed.'));
		} finally {
			uploadingPhotoFor = null;
		}
	}

	// Lazily fetch (authed) and cache a thumbnail object URL for a stored photo.
	async function loadPhotoUrl(storedFileId: number) {
		if (photoUrls[storedFileId]) return;
		try {
			const url = await fileObjectUrl(storedFileId, { thumb: true });
			photoUrls = { ...photoUrls, [storedFileId]: url };
		} catch {
			// Thumbnail is best-effort; ignore failures.
		}
	}

	// Whenever the item list changes, make sure every attached photo has a URL.
	$effect(() => {
		for (const item of inspection?.items ?? []) {
			if (item.photoStoredFileId) loadPhotoUrl(item.photoStoredFileId);
		}
	});

	// --- Complete inspection ---
	let showCompleteConfirm = $state(false);
	let completeResult = $state<InspectionCompleteResult | null>(null);

	const completeMutation = createMutation(() => ({
		mutationFn: () => inspections.complete(id),
		onSuccess: (result) => {
			completeResult = result;
			showCompleteConfirm = false;
			showSuccess('Inspection completed.');
			invalidate();
		},
		onError: (err) => {
			showCompleteConfirm = false;
			showError(apiErrorMessage(err, 'Could not complete the inspection.'));
		},
	}));

	// Combine the just-completed result with any IDs already on a reloaded inspection.
	const createdWorkOrderIds = $derived(completeResult?.createdWorkOrderIds ?? []);

	// --- Report download ---
	let downloadingReport = $state(false);
	async function downloadReport() {
		downloadingReport = true;
		try {
			await openInspectionReport(id);
		} catch (err) {
			showError(apiErrorMessage(err, 'Could not open the report.'));
		} finally {
			downloadingReport = false;
		}
	}

	function formatDate(val: string | undefined | null): string {
		if (!val) return '—';
		const d = new Date(val);
		return isNaN(d.getTime()) ? val : d.toLocaleString();
	}

	const RESULT_BADGE: Record<string, { class: string }> = {
		Pass: { class: 'm3-tone-chip border m3-tone--success' },
		Fail: { class: 'm3-tone-chip border m3-tone--error' },
		NotApplicable: { class: 'bg-muted text-muted-foreground border-border' },
		Pending: { class: 'm3-tone-chip border m3-tone--info' },
	};
</script>

<svelte:head>
	<title>{inspection ? `${inspection.type} inspection` : 'Inspection'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-24" data-testid="inspection-detail-page">
	<div class="mb-4">
		<PageBreadcrumb
			crumbs={[
				{ label: 'Work Orders', href: '/maintenance' },
				{ label: inspection ? `${inspection.type} inspection` : 'Inspection' },
			]}
		/>
	</div>

	{#if inspectionQuery.isLoading}
		<p class="py-8 text-center text-sm text-muted-foreground" data-testid="inspection-detail-loading">Loading…</p>
	{:else if inspectionQuery.isError}
		<p class="py-8 text-center text-sm text-destructive" data-testid="inspection-detail-error">Failed to load inspection.</p>
	{:else if !inspection}
		<p class="py-8 text-center text-sm text-muted-foreground" data-testid="inspection-detail-not-found">Inspection not found.</p>
	{:else}
		<!-- Header -->
		<div class="mb-6 flex flex-wrap items-start justify-between gap-4">
			<div class="space-y-2">
				<div class="flex flex-wrap items-center gap-2">
					<h1 class="text-2xl font-bold" data-testid="inspection-detail-title">{inspection.type} inspection</h1>
					<StatusBadge status={inspection.status} />
				</div>
				<div class="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-muted-foreground">
					{#if inspection.propertyName}
						<span data-testid="inspection-detail-property">{inspection.propertyName}</span>
					{/if}
					{#if inspection.unitNumber}
						<span data-testid="inspection-detail-unit">Unit {inspection.unitNumber}</span>
					{/if}
					<span data-testid="inspection-detail-scheduled">Scheduled {formatDate(inspection.scheduledFor)}</span>
					{#if inspection.inspector}
						<span data-testid="inspection-detail-inspector">Inspector: {inspection.inspector}</span>
					{/if}
				</div>
			</div>
			<div class="flex flex-wrap items-center gap-2">
				{#if isCompleted || inspection.reportStoredFileId}
					<Button
						variant="outline"
						onclick={downloadReport}
						disabled={downloadingReport}
						data-testid="inspection-download-report"
					>
						<Download class="h-4 w-4" />
						{downloadingReport ? 'Opening…' : 'Download report'}
					</Button>
				{/if}
				{#if !isCompleted}
					<Button
						onclick={() => (showCompleteConfirm = true)}
						disabled={inspection.items.length === 0}
						data-testid="inspection-complete-button"
					>
						<ClipboardCheck class="h-4 w-4" />
						Complete inspection
					</Button>
				{/if}
			</div>
		</div>

		<!-- Progress summary -->
		<div class="mb-6 grid grid-cols-2 gap-3 sm:grid-cols-4" data-testid="inspection-progress">
			<div class="rounded-md border border-border bg-background p-3 text-center">
				<p class="text-2xl font-bold tabular-nums text-[var(--success)]" data-testid="inspection-pass-count">{counts.pass}</p>
				<p class="text-xs uppercase tracking-wide text-muted-foreground">Pass</p>
			</div>
			<div class="rounded-md border border-border bg-background p-3 text-center">
				<p class="text-2xl font-bold tabular-nums text-[var(--m3c-error)]" data-testid="inspection-fail-count">{counts.fail}</p>
				<p class="text-xs uppercase tracking-wide text-muted-foreground">Fail</p>
			</div>
			<div class="rounded-md border border-border bg-background p-3 text-center">
				<p class="text-2xl font-bold tabular-nums text-muted-foreground" data-testid="inspection-na-count">{counts.na}</p>
				<p class="text-xs uppercase tracking-wide text-muted-foreground">N/A</p>
			</div>
			<div class="rounded-md border border-border bg-background p-3 text-center">
				<p class="text-2xl font-bold tabular-nums text-[var(--info)]" data-testid="inspection-pending-count">{counts.pending}</p>
				<p class="text-xs uppercase tracking-wide text-muted-foreground">To do</p>
			</div>
		</div>

		<!-- Completion summary banner -->
		{#if isCompleted}
			<div
				class="mb-6 flex items-start gap-3 rounded-md border bg-[var(--m3c-success-container)] text-[var(--m3c-on-success-container)] border-[color-mix(in_srgb,var(--success)_45%,transparent)] p-4 text-sm"
				data-testid="inspection-completed-banner"
			>
				<Check class="mt-0.5 h-4 w-4 shrink-0" />
				<div>
					<p class="font-medium">This inspection is complete.</p>
					<p class="mt-0.5">
						{counts.pass} passed · {counts.fail} failed · {counts.na} N/A. The checklist is now read-only.
					</p>
					{#if createdWorkOrderIds.length > 0}
						<p class="mt-2">
							Created {createdWorkOrderIds.length} work order{createdWorkOrderIds.length === 1 ? '' : 's'} for the failed items:
						</p>
						<div class="mt-1 flex flex-wrap gap-2" data-testid="inspection-created-work-orders">
							{#each createdWorkOrderIds as woId (woId)}
								<a
									href={recordHref('workOrder', { id: woId, unitId: inspection.unitId })}
									class="m3-tone-chip border m3-tone--success rounded px-2 py-1 text-xs font-medium"
									data-testid="inspection-work-order-link-{woId}"
								>
									Work order #{woId}
								</a>
							{/each}
						</div>
					{/if}
				</div>
			</div>
		{/if}

		{#if !readOnly}
			<Card.Root class="mb-6 gap-0 py-0" data-testid="inspection-add-question-card">
				<Card.Header class="border-b border-border px-4 py-3">
					<Card.Title class="text-base font-semibold">Add checklist question</Card.Title>
				</Card.Header>
				<Card.Content class="p-4">
					<div class="grid gap-3 lg:grid-cols-[minmax(0,14rem)_minmax(0,1fr)_auto] lg:items-start">
						<div>
							<label for="inspection-new-question-area" class="mb-1 block text-xs font-medium text-muted-foreground">Area</label>
							<Input
								id="inspection-new-question-area"
								bind:value={addQuestionDraft.area}
								placeholder="Kitchen"
								aria-invalid={!!addQuestionErrors.area}
								oninput={() => (addQuestionErrors = { ...addQuestionErrors, area: '' })}
								data-testid="inspection-new-question-area"
							/>
							{#if addQuestionErrors.area}
								<p class="mt-1 text-xs text-destructive" data-testid="inspection-new-question-area-error">{addQuestionErrors.area}</p>
							{/if}
						</div>
						<div>
							<label for="inspection-new-question-label" class="mb-1 block text-xs font-medium text-muted-foreground">Question</label>
							<Input
								id="inspection-new-question-label"
								bind:value={addQuestionDraft.label}
								placeholder="Sink and faucet are dry"
								aria-invalid={!!addQuestionErrors.label}
								oninput={() => (addQuestionErrors = { ...addQuestionErrors, label: '' })}
								data-testid="inspection-new-question-label"
							/>
							{#if addQuestionErrors.label}
								<p class="mt-1 text-xs text-destructive" data-testid="inspection-new-question-label-error">{addQuestionErrors.label}</p>
							{/if}
						</div>
						<Button
							class="mt-5"
							onclick={addQuestion}
							disabled={createQuestionMutation.isPending}
							data-testid="inspection-new-question-save"
						>
							<Plus class="h-4 w-4" />
							{createQuestionMutation.isPending ? 'Adding…' : 'Add question'}
						</Button>
					</div>
				</Card.Content>
			</Card.Root>
		{/if}

		<!-- Checklist grouped by area -->
		{#if inspection.items.length === 0}
			<Card.Root>
				<Card.Content class="py-10 text-center text-sm text-muted-foreground" data-testid="inspection-checklist-empty">
					This inspection has no checklist items. (No template was selected when it was scheduled.)
				</Card.Content>
			</Card.Root>
		{:else}
			<div class="space-y-6" data-testid="inspection-checklist">
				{#each groupedItems as group (group.area)}
					<Card.Root class="gap-0 py-0">
						<Card.Header class="border-b border-border px-4 py-3">
							<Card.Title class="text-base font-semibold" data-testid="inspection-area-heading">{group.area}</Card.Title>
						</Card.Header>
						<Card.Content class="divide-y divide-border p-0">
							{#each group.items as item (item.id)}
								<div class="space-y-3 p-4" data-testid="inspection-item-{item.id}">
									<div class="flex flex-wrap items-center justify-between gap-2">
										{#if editingQuestionId === item.id}
											<div class="grid min-w-0 flex-1 gap-3 lg:grid-cols-[minmax(0,14rem)_minmax(0,1fr)]">
												<div>
													<label for="inspection-item-{item.id}-area" class="mb-1 block text-xs font-medium text-muted-foreground">Area</label>
													<Input
														id="inspection-item-{item.id}-area"
														bind:value={editQuestionDrafts[item.id].area}
														aria-invalid={!!editQuestionErrors[item.id]?.area}
														oninput={() =>
															(editQuestionErrors = {
																...editQuestionErrors,
																[item.id]: { ...(editQuestionErrors[item.id] ?? {}), area: '' },
															})}
														data-testid="inspection-item-{item.id}-area-input"
													/>
													{#if editQuestionErrors[item.id]?.area}
														<p class="mt-1 text-xs text-destructive" data-testid="inspection-item-{item.id}-area-error">
															{editQuestionErrors[item.id].area}
														</p>
													{/if}
												</div>
												<div>
													<label for="inspection-item-{item.id}-label-input" class="mb-1 block text-xs font-medium text-muted-foreground">Question</label>
													<Input
														id="inspection-item-{item.id}-label-input"
														bind:value={editQuestionDrafts[item.id].label}
														aria-invalid={!!editQuestionErrors[item.id]?.label}
														oninput={() =>
															(editQuestionErrors = {
																...editQuestionErrors,
																[item.id]: { ...(editQuestionErrors[item.id] ?? {}), label: '' },
															})}
														data-testid="inspection-item-{item.id}-label-input"
													/>
													{#if editQuestionErrors[item.id]?.label}
														<p class="mt-1 text-xs text-destructive" data-testid="inspection-item-{item.id}-label-error">
															{editQuestionErrors[item.id].label}
														</p>
													{/if}
												</div>
											</div>
											<div class="flex flex-wrap items-center gap-2">
												<Button
													size="sm"
													onclick={() => saveQuestion(item)}
													disabled={updateQuestionMutation.isPending}
													data-testid="inspection-item-{item.id}-question-save"
												>
													<Save class="h-4 w-4" />
													Save
												</Button>
												<Button
													variant="outline"
													size="sm"
													onclick={() => cancelEditingQuestion(item.id)}
													disabled={updateQuestionMutation.isPending}
													data-testid="inspection-item-{item.id}-question-cancel"
												>
													Cancel
												</Button>
											</div>
										{:else}
											<div class="min-w-0">
												<p class="font-medium" data-testid="inspection-item-label">{item.label}</p>
												<p class="mt-0.5 text-xs text-muted-foreground" data-testid="inspection-item-area">{item.area}</p>
											</div>
											<div class="flex flex-wrap items-center gap-2">
												<StatusBadge status={item.result} map={RESULT_BADGE} />
												{#if !readOnly}
													<Button
														variant="ghost"
														size="icon-sm"
														aria-label="Move question up"
														disabled={orderedItems.findIndex((it) => it.id === item.id) <= 0 || reorderingQuestionId === item.id}
														onclick={() => moveQuestion(item, -1)}
														data-testid="inspection-item-{item.id}-move-up"
													>
														<ArrowUp class="h-4 w-4" />
													</Button>
													<Button
														variant="ghost"
														size="icon-sm"
														aria-label="Move question down"
														disabled={orderedItems.findIndex((it) => it.id === item.id) >= orderedItems.length - 1 || reorderingQuestionId === item.id}
														onclick={() => moveQuestion(item, 1)}
														data-testid="inspection-item-{item.id}-move-down"
													>
														<ArrowDown class="h-4 w-4" />
													</Button>
													<Button
														variant="outline"
														size="sm"
														onclick={() => startEditingQuestion(item)}
														data-testid="inspection-item-{item.id}-question-edit"
													>
														<Edit2 class="h-4 w-4" />
														Edit
													</Button>
													<Button
														variant="ghost"
														size="icon-sm"
														class="hover:text-destructive"
														aria-label="Delete question"
														disabled={deletingQuestionId === item.id}
														onclick={() => deleteQuestion(item)}
														data-testid="inspection-item-{item.id}-question-delete"
													>
														<Trash2 class="h-4 w-4" />
													</Button>
												{/if}
											</div>
										{/if}
									</div>

									<!-- Pass / Fail / N/A — big touch targets -->
									<div class="grid grid-cols-3 gap-2">
										<Button
											type="button"
											variant={item.result === 'Pass' ? 'default' : 'outline'}
											class="h-12 {item.result === 'Pass' ? 'bg-[var(--success)] text-[var(--success-foreground)] hover:bg-[color-mix(in_srgb,var(--success)_88%,black)]' : ''}"
											disabled={readOnly || savingResultFor === item.id}
											onclick={() => setResult(item, 'Pass')}
											data-testid="inspection-item-{item.id}-pass"
										>
											<Check class="h-5 w-5" /> Pass
										</Button>
										<Button
											type="button"
											variant={item.result === 'Fail' ? 'destructive' : 'outline'}
											class="h-12"
											disabled={readOnly || savingResultFor === item.id}
											onclick={() => setResult(item, 'Fail')}
											data-testid="inspection-item-{item.id}-fail"
										>
											<X class="h-5 w-5" /> Fail
										</Button>
										<Button
											type="button"
											variant={item.result === 'NotApplicable' ? 'secondary' : 'outline'}
											class="h-12"
											disabled={readOnly || savingResultFor === item.id}
											onclick={() => setResult(item, 'NotApplicable')}
											data-testid="inspection-item-{item.id}-na"
										>
											<Minus class="h-5 w-5" /> N/A
										</Button>
									</div>

									<!-- Note -->
									<div>
										<label for="note-{item.id}" class="mb-1 block text-xs font-medium text-muted-foreground">Note (optional)</label>
										<textarea
											id="note-{item.id}"
											rows={2}
											class="w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
											placeholder="Add a note about this item…"
											disabled={readOnly}
											value={noteValue(item)}
											oninput={(e) => (noteDrafts[item.id] = (e.currentTarget as HTMLTextAreaElement).value)}
											onblur={() => saveNote(item)}
											data-testid="inspection-item-{item.id}-note"
										></textarea>
									</div>

									<!-- Photo -->
									<div class="flex items-center gap-3">
										{#if item.photoStoredFileId}
											{#if photoUrls[item.photoStoredFileId]}
												<a
													href={photoUrls[item.photoStoredFileId]}
													target="_blank"
													rel="noopener"
													class="block"
													data-testid="inspection-item-{item.id}-photo-link"
												>
													<img
														src={photoUrls[item.photoStoredFileId]}
														alt="Inspection photo for {item.label}"
														class="h-16 w-16 rounded-md border border-border object-cover"
														data-testid="inspection-item-{item.id}-photo-thumb"
													/>
												</a>
											{:else}
												<div class="flex h-16 w-16 items-center justify-center rounded-md border border-border bg-muted text-xs text-muted-foreground">
													…
												</div>
											{/if}
										{/if}
										{#if !readOnly}
											<Button
												type="button"
												variant="outline"
												onclick={() => triggerPhoto(item)}
												disabled={uploadingPhotoFor === item.id}
												data-testid="inspection-item-{item.id}-photo-button"
											>
												<Camera class="h-4 w-4" />
												{uploadingPhotoFor === item.id
													? 'Uploading…'
													: item.photoStoredFileId
														? 'Replace photo'
														: 'Add photo'}
											</Button>
											<input
												bind:this={photoInputs[item.id]}
												type="file"
												accept="image/*"
												capture="environment"
												class="hidden"
												onchange={(e) => handlePhoto(item, e)}
												data-testid="inspection-item-{item.id}-photo-input"
											/>
										{/if}
									</div>
								</div>
							{/each}
						</Card.Content>
					</Card.Root>
				{/each}
			</div>
		{/if}
	{/if}
</div>

<!-- Complete inspection confirmation -->
<Dialog.Root open={showCompleteConfirm} onOpenChange={(v) => { if (!v) showCompleteConfirm = false; }}>
	<Dialog.Content data-testid="inspection-complete-dialog">
		<Dialog.Header>
			<Dialog.Title>Complete this inspection?</Dialog.Title>
			<Dialog.Description>
				We'll lock the checklist, generate a PDF report, and open work orders for any items marked
				<span class="font-medium">Fail</span>. This can't be undone.
			</Dialog.Description>
		</Dialog.Header>
		{#if inspection}
			<p class="text-sm text-muted-foreground">
				{counts.pass} pass · {counts.fail} fail · {counts.na} N/A
				{#if counts.pending > 0}
					· <span class="font-medium text-foreground">{counts.pending} still to do</span>
				{/if}
			</p>
		{/if}
		<Dialog.Footer>
			<Button
				variant="outline"
				onclick={() => (showCompleteConfirm = false)}
				disabled={completeMutation.isPending}
				data-testid="inspection-complete-cancel"
			>
				Cancel
			</Button>
			<Button
				onclick={() => completeMutation.mutate()}
				disabled={completeMutation.isPending}
				data-testid="inspection-complete-confirm"
			>
				{completeMutation.isPending ? 'Completing…' : 'Complete inspection'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
