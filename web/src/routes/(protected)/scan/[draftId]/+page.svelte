<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { toast } from 'svelte-sonner';
	import { scan, type ScanFieldDto } from '$lib/api/scan';

	// ScheduleECategory enum values (mirrors RentalCommand.Core.Enums.ScheduleECategory)
	const SCHEDULE_E_CATEGORIES = [
		'Advertising',
		'AutoTravel',
		'CleaningMaintenance',
		'Commissions',
		'Insurance',
		'LegalProfessional',
		'ManagementFees',
		'MortgageInterest',
		'Repairs',
		'Supplies',
		'Taxes',
		'Utilities',
		'Depreciation',
		'Other'
	] as const;

	const queryClient = useQueryClient();

	const draftId = $derived(parseInt($page.params.draftId ?? '0', 10));

	const draftQuery = createQuery(() => ({
		queryKey: ['scan', draftId],
		queryFn: () => scan.get(draftId),
		// Bounded poll: only while Pending (SignalR flips to Reviewing; poll is a fallback)
		refetchInterval: (q) => (q.state.data?.status === 'Pending' ? 1500 : false)
	}));

	const data = $derived(draftQuery.data);

	// Editable field values (keyed by field name)
	let editedFields = $state<Record<string, string>>({});

	// Initialize editable fields when data arrives
	$effect(() => {
		if (data?.fields) {
			const initial: Record<string, string> = {};
			for (const f of data.fields) {
				if (!(f.name in editedFields)) {
					initial[f.name] = f.value;
				}
			}
			if (Object.keys(initial).length > 0) {
				editedFields = { ...editedFields, ...initial };
			}
		}
	});

	// Svelte action to focus the first low-confidence field
	let firstLowFocused = $state(false);
	function focusFirstLow(node: HTMLElement, isFirstLow: boolean) {
		if (isFirstLow && !firstLowFocused) {
			firstLowFocused = true;
			// defer to next tick so the DOM is ready
			setTimeout(() => node.focus(), 0);
		}
		return {};
	}

	function confidenceLevel(confidence: number): 'high' | 'medium' | 'low' {
		if (confidence >= 0.8) return 'high';
		if (confidence >= 0.5) return 'medium';
		return 'low';
	}

	function confidenceLabel(confidence: number): string {
		const level = confidenceLevel(confidence);
		if (level === 'medium') return 'Medium confidence';
		if (level === 'low') return 'Low confidence';
		return '';
	}

	function fieldInputClass(field: ScanFieldDto): string {
		const level = confidenceLevel(field.confidence);
		const base = 'w-full rounded border px-3 py-2 text-sm bg-bg text-text focus:outline-none focus:ring-1 focus:ring-accent';
		if (level === 'low') return `${base} border-red-400 dark:border-red-500`;
		if (level === 'medium') return `${base} border-border text-text-secondary`;
		return `${base} border-border`;
	}

	function isFirstLowField(fields: ScanFieldDto[], field: ScanFieldDto): boolean {
		const firstLow = fields.find((f) => confidenceLevel(f.confidence) === 'low');
		return firstLow?.name === field.name;
	}

	const isImage = $derived(() => {
		const url = data?.fileUrl ?? '';
		// Heuristic: if the file URL is for an image content type
		// We check the field list for a hint, otherwise rely on extension
		// The API serves /api/v1/scans/{id}/file — we use an img tag and let it fail gracefully
		return true; // default to image; <img> won't crash on PDFs
	});

	const fileUrl = $derived(data ? `/api/v1/scans/${data.id}/file` : '');

	// Confirm mutation
	const confirmMutation = createMutation(() => ({
		mutationFn: () => {
			const overrides = buildOverridesJson();
			return scan.confirm(draftId, overrides);
		},
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			queryClient.invalidateQueries({ queryKey: ['scan', draftId] });
			toast.success('Expense created');
			goto('/accounting');
		},
		onError: (err) => {
			toast.error(err instanceof Error ? err.message : 'Confirm failed');
		}
	}));

	// Reject mutation
	const rejectMutation = createMutation(() => ({
		mutationFn: (reason: string) => scan.reject(draftId, reason),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			toast.success('Scan rejected');
			goto('/scan');
		},
		onError: (err) => {
			toast.error(err instanceof Error ? err.message : 'Reject failed');
		}
	}));

	function buildOverridesJson(): string {
		// Build flat overrides object mirroring the API's ApplyOverrides expected shape
		// Map camelCase field names to the override format
		const overrides: Record<string, unknown> = {};
		for (const [name, value] of Object.entries(editedFields)) {
			overrides[name] = value;
		}
		return JSON.stringify(overrides);
	}

	function handleReject() {
		const reason = window.prompt('Enter a reason for rejection (optional):') ?? '';
		rejectMutation.mutate(reason);
	}
</script>

<svelte:head>
	<title>Review Scan - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="scan-review">
	{#if draftQuery.isLoading}
		<div class="flex h-48 items-center justify-center">
			<div class="h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"></div>
		</div>
	{:else if !data}
		<p class="text-sm text-text-secondary">Scan draft not found.</p>
	{:else}
		<div class="mb-4 flex items-center gap-3">
			<a href="/scan" class="text-sm text-text-secondary hover:text-text">&larr; Back to Scans</a>
			<span class="text-text-secondary">/</span>
			<h1 class="text-xl font-bold">Review Scan #{data.id}</h1>
			<span class="inline-flex rounded-full px-2 py-0.5 text-xs font-medium
				{data.status === 'Pending' ? 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300' : ''}
				{data.status === 'Reviewing' ? 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300' : ''}
				{data.status === 'Confirmed' ? 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300' : ''}
				{data.status === 'Failed' || data.status === 'Rejected' ? 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-300' : ''}">
				{data.status}
			</span>
		</div>

		{#if data.status === 'Failed'}
			<div class="mb-4 rounded-lg border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-800 dark:border-red-700 dark:bg-red-900/20 dark:text-red-300">
				<strong>Extraction failed.</strong> You can still manually enter the field values below and confirm.
			</div>
		{/if}

		{#if data.status === 'Pending'}
			<div class="mb-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-300">
				Processing document… fields will appear once extraction completes.
			</div>
		{/if}

		<div class="grid gap-6 lg:grid-cols-2">
			<!-- Left: document preview -->
			<div class="flex flex-col rounded-lg border border-border bg-surface">
				<div class="border-b border-border px-4 py-3">
					<span class="font-semibold text-sm">Document Preview</span>
				</div>
				<div class="flex flex-1 items-center justify-center overflow-hidden p-4">
					{#if fileUrl}
						<!-- Try img first; for PDFs it won't render but we also show an iframe/link -->
						<div class="w-full">
							<img
								src={fileUrl}
								alt="Scanned document"
								class="max-h-[60vh] w-full rounded object-contain"
								onerror={(e) => {
									// If image fails (it's a PDF), hide img and show iframe
									(e.currentTarget as HTMLImageElement).style.display = 'none';
									(e.currentTarget.nextElementSibling as HTMLElement | null)?.removeAttribute('hidden');
								}}
							/>
							<iframe
								hidden
								src={fileUrl}
								title="Scanned document"
								class="h-[60vh] w-full rounded border-0"
							></iframe>
							<div class="mt-2 text-center">
								<a
									href={fileUrl}
									target="_blank"
									rel="noopener noreferrer"
									class="text-xs text-accent hover:underline"
								>
									Open in new tab
								</a>
							</div>
						</div>
					{:else}
						<p class="text-sm text-text-secondary">No preview available.</p>
					{/if}
				</div>
			</div>

			<!-- Right: extracted fields form -->
			<div class="flex flex-col rounded-lg border border-border bg-surface">
				<div class="border-b border-border px-4 py-3">
					<span class="font-semibold text-sm">Extracted Fields</span>
					{#if data.modelId}
						<span class="ml-2 text-xs text-text-secondary">via {data.modelId}</span>
					{/if}
				</div>
				<div class="flex-1 space-y-4 overflow-y-auto p-4">
					{#if data.fields.length === 0}
						<p class="text-sm text-text-secondary">
							{#if data.status === 'Pending'}
								Fields will appear once extraction completes.
							{:else}
								No fields extracted. Enter values manually below.
							{/if}
						</p>
						<!-- Manual entry fallback: provide common fields -->
						<div class="space-y-3">
							{#each ['vendor_name', 'amount', 'transaction_date', 'category', 'notes'] as fieldName}
								<div>
									<label class="mb-1 block text-xs font-medium text-text-secondary capitalize" for="field-{fieldName}">
										{fieldName.replace(/_/g, ' ')}
									</label>
									{#if fieldName === 'category'}
										<select
											id="field-{fieldName}"
											data-testid="scan-field-{fieldName}"
											bind:value={editedFields[fieldName]}
											class="w-full rounded border border-border bg-bg px-3 py-2 text-sm text-text focus:outline-none focus:ring-1 focus:ring-accent"
										>
											<option value="">Select category</option>
											{#each SCHEDULE_E_CATEGORIES as cat}
												<option value={cat}>{cat}</option>
											{/each}
										</select>
									{:else}
										<input
											id="field-{fieldName}"
											data-testid="scan-field-{fieldName}"
											type="text"
											bind:value={editedFields[fieldName]}
											class="w-full rounded border border-border bg-bg px-3 py-2 text-sm text-text focus:outline-none focus:ring-1 focus:ring-accent"
										/>
									{/if}
								</div>
							{/each}
						</div>
					{:else}
						{#each data.fields as field (field.name)}
							{@const level = confidenceLevel(field.confidence)}
							<div>
								<div class="mb-1 flex items-center justify-between">
									<label
										class="text-xs font-medium capitalize {level === 'medium' ? 'text-text-secondary' : 'text-text'}"
										for="field-{field.name}"
									>
										{field.name.replace(/_/g, ' ')}
									</label>
									{#if level !== 'high'}
										<span class="text-xs {level === 'low' ? 'text-red-500' : 'text-text-secondary'}">
											{confidenceLabel(field.confidence)}
										</span>
									{/if}
								</div>
								{#if field.name === 'category'}
									<select
										id="field-{field.name}"
										data-testid="scan-field-{field.name}"
										bind:value={editedFields[field.name]}
										class={fieldInputClass(field)}
									>
										<option value="">Select category</option>
										{#each SCHEDULE_E_CATEGORIES as cat}
											<option value={cat}>{cat}</option>
										{/each}
									</select>
								{:else}
									<input
										id="field-{field.name}"
										data-testid="scan-field-{field.name}"
										type="text"
										bind:value={editedFields[field.name]}
										class={fieldInputClass(field)}
										use:focusFirstLow={level === 'low' && isFirstLowField(data.fields, field)}
									/>
								{/if}
							</div>
						{/each}
					{/if}
				</div>

				<!-- Action buttons -->
				<div class="border-t border-border px-4 py-3">
					<div class="flex gap-3">
						<button
							data-testid="scan-confirm"
							onclick={() => confirmMutation.mutate()}
							disabled={confirmMutation.isPending || data.status === 'Rejected'}
							class="flex-1 rounded bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent/90 disabled:cursor-not-allowed disabled:opacity-50"
						>
							{confirmMutation.isPending ? 'Confirming…' : 'Confirm & Create Expense'}
						</button>
						<button
							data-testid="scan-reject"
							onclick={handleReject}
							disabled={rejectMutation.isPending || data.status === 'Rejected' || data.status === 'Confirmed'}
							class="rounded border border-border px-4 py-2 text-sm font-medium text-text-secondary hover:bg-surface-hover hover:text-danger disabled:cursor-not-allowed disabled:opacity-50"
						>
							{rejectMutation.isPending ? 'Rejecting…' : 'Reject'}
						</button>
					</div>
					{#if data.status === 'Confirmed'}
						<p class="mt-2 text-center text-xs text-green-600 dark:text-green-400">This scan has already been confirmed.</p>
					{/if}
					{#if data.status === 'Rejected'}
						<p class="mt-2 text-center text-xs text-text-secondary">This scan has been rejected.</p>
					{/if}
				</div>
			</div>
		</div>
	{/if}
</div>
