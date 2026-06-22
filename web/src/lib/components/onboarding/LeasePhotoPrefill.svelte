<script lang="ts">
	import { createQuery, createMutation } from '@tanstack/svelte-query';
	import { Camera, Loader2, Check, X, ArrowRight } from '@lucide/svelte';
	import { scan, type ScanDraftResponse, type ScanFieldDto } from '$lib/api/scan';
	import { Button } from '$lib/components/ui/button';
	import FileDrop from '$lib/components/FileDrop.svelte';
	import { showError, apiErrorMessage } from '$lib/utils/toast';

	/**
	 * Snap-a-photo pre-fill for the lease step. Routes through the EXISTING scan pipeline
	 * (`targetEntityType: 'Lease'`, the same path "import your PDF leases" uses) — no parallel
	 * extraction. The user CONFIRMS each extracted value before it is applied; nothing is silently
	 * committed. Confidence is shown so a non-technical landlord knows what to double-check.
	 *
	 * The lease extraction schema (LeaseExtractionSchema.cs) returns these field names:
	 *   lease_number, start_date, end_date, monthly_rent, security_deposit, late_fee,
	 *   rent_due_day, tenant_name, property_id, unit_id
	 * The parent maps the confirmed subset onto its lease form via `onapply`.
	 */
	type PrefillValues = {
		leaseNumber?: string;
		startDate?: string;
		endDate?: string;
		monthlyRent?: string;
		securityDeposit?: string;
		lateFee?: string;
		rentDueDay?: string;
	};

	type PrefillApplyValues = PrefillValues & { draftId: number };

	let { onapply }: { onapply: (values: PrefillApplyValues) => void } = $props();

	// Field-name → friendly label + which form field it feeds. property_id/unit_id/tenant_name are
	// captured by the schema but the wizard's lease step uses its own pickers, so we surface only the
	// scalar term fields here (the ones a landlord would otherwise type by hand).
	const TERM_FIELDS: { name: string; label: string; key: keyof PrefillValues }[] = [
		{ name: 'lease_number', label: 'Lease number', key: 'leaseNumber' },
		{ name: 'start_date', label: 'Start date', key: 'startDate' },
		{ name: 'end_date', label: 'End date', key: 'endDate' },
		{ name: 'monthly_rent', label: 'Monthly rent', key: 'monthlyRent' },
		{ name: 'security_deposit', label: 'Security deposit', key: 'securityDeposit' },
		{ name: 'late_fee', label: 'Late fee', key: 'lateFee' },
		{ name: 'rent_due_day', label: 'Rent due day', key: 'rentDueDay' }
	];

	let draftId = $state<number | null>(null);
	let open = $state(false);
	// Per-row include/value the user reviews before applying.
	let review = $state<{ name: string; label: string; key: keyof PrefillValues; value: string; confidence: number; include: boolean }[]>([]);

	const uploadMutation = createMutation(() => ({
		mutationFn: (file: File) => scan.upload(file, 'Lease'),
		onSuccess: (res) => {
			draftId = res.draftId;
			review = [];
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not read that file. Try a clearer photo or the PDF.'))
	}));

	// Poll the draft while the worker reads the document (Pending → Processing → Reviewing).
	const draftQuery = createQuery(() => ({
		queryKey: ['onboarding-lease-prefill', draftId],
		enabled: draftId != null,
		queryFn: () => scan.get(draftId as number),
		refetchInterval: (q) => {
			const s = q.state.data?.status;
			return s === 'Pending' || s === 'Processing' ? 1200 : false;
		}
	}));

	const isProcessing = $derived(
		draftId != null && (draftQuery.data?.status === 'Pending' || draftQuery.data?.status === 'Processing' || (draftQuery.isLoading && !draftQuery.data))
	);
	const isFailed = $derived(draftQuery.data?.status === 'Failed' || draftQuery.data?.status === 'Rejected');

	// When extraction finishes, build the review rows once (default-include anything non-blank).
	$effect(() => {
		const data = draftQuery.data;
		if (!data || data.status !== 'Reviewing' || review.length > 0) return;
		const byName = new Map<string, ScanFieldDto>(data.fields.map((f) => [f.name, f]));
		const rows = TERM_FIELDS
			.map((f) => {
				const got = byName.get(f.name);
				const value = (got?.value ?? '').trim();
				if (!value) return null;
				return { name: f.name, label: f.label, key: f.key, value, confidence: got?.confidence ?? 0, include: true };
			})
			.filter((r): r is NonNullable<typeof r> => r !== null);
		review = rows;
	});

	function handleFile(file: File) {
		uploadMutation.mutate(file);
	}

	function applyConfirmed() {
		if (draftId == null) return;
		const values: PrefillValues = {};
		for (const row of review) {
			if (row.include) values[row.key] = row.value;
		}
		onapply({ ...values, draftId });
		reset();
	}

	function reset() {
		draftId = null;
		review = [];
		open = false;
	}

	function confidencePct(c: number) {
		return Math.round((c <= 1 ? c * 100 : c));
	}
</script>

<div class="rounded-[var(--m3-shape-large,0.75rem)] border border-accent/40 bg-accent/5 p-3" data-testid="lease-photo-prefill">
	{#if !open}
		<div class="flex flex-wrap items-center justify-between gap-2">
			<div class="flex items-center gap-2">
				<span class="flex h-8 w-8 items-center justify-center rounded-full bg-primary/15 text-primary">
					<Camera class="h-4 w-4" />
				</span>
				<div>
					<p class="text-sm font-medium text-foreground">Have the lease on paper?</p>
					<p class="text-xs text-muted-foreground">Snap a photo and we'll fill in the rent, dates, and deposit for you to confirm.</p>
				</div>
			</div>
			<Button variant="outline" size="sm" class="gap-1.5" data-testid="lease-photo-prefill-start" onclick={() => (open = true)}>
				<Camera class="h-4 w-4" />
				Snap a photo
			</Button>
		</div>
	{:else if uploadMutation.isPending || isProcessing}
		<div class="flex items-center gap-2 px-1 py-3 text-sm text-muted-foreground" data-testid="lease-photo-prefill-processing">
			<Loader2 class="h-4 w-4 animate-spin" />
			Reading your lease… this takes a few seconds.
		</div>
	{:else if isFailed}
		<div class="space-y-2" data-testid="lease-photo-prefill-failed">
			<p class="text-sm text-foreground">We couldn't read that one. Try a clearer, well-lit photo, or just type the values in below.</p>
			<div class="flex gap-2">
				<Button variant="outline" size="sm" onclick={reset}>Try a different file</Button>
			</div>
		</div>
	{:else if review.length > 0}
		<div class="space-y-3" data-testid="lease-photo-prefill-review">
			<p class="text-sm font-medium text-foreground">Here's what we read — uncheck anything that looks wrong, then apply.</p>
			<div class="space-y-1.5">
				{#each review as row (row.name)}
					<label class="flex items-center justify-between gap-3 rounded-md border border-border bg-background px-3 py-2" data-testid={`lease-prefill-row-${row.name}`}>
						<span class="flex min-w-0 items-center gap-2">
							<input
								type="checkbox"
								class="h-4 w-4 shrink-0 accent-[var(--primary)]"
								bind:checked={row.include}
								data-testid={`lease-prefill-include-${row.name}`}
							/>
							<span class="min-w-0">
								<span class="block text-xs text-muted-foreground">{row.label}</span>
								<span class="block truncate text-sm font-medium text-foreground">{row.value}</span>
							</span>
						</span>
						<span
							class="shrink-0 rounded-full px-2 py-0.5 text-[11px] font-medium {confidencePct(row.confidence) >= 80
								? 'bg-success/15 text-success'
								: confidencePct(row.confidence) >= 50
									? 'bg-warning/15 text-warning'
									: 'bg-destructive/15 text-destructive'}"
							title="How sure the computer is"
						>
							{confidencePct(row.confidence)}% sure
						</span>
					</label>
				{/each}
			</div>
			<div class="flex items-center gap-2">
				<Button size="sm" class="gap-1.5" data-testid="lease-photo-prefill-apply" onclick={applyConfirmed}>
					<Check class="h-4 w-4" />
					Use these values
				</Button>
				<Button variant="ghost" size="sm" class="gap-1.5" data-testid="lease-photo-prefill-cancel" onclick={reset}>
					<X class="h-4 w-4" />
					Cancel
				</Button>
			</div>
		</div>
	{:else if draftQuery.data?.status === 'Reviewing'}
		<div class="space-y-2" data-testid="lease-photo-prefill-empty">
			<p class="text-sm text-foreground">We read the file but couldn't pull out the lease terms. You can type them in below.</p>
			<Button variant="outline" size="sm" onclick={reset}>Try a different file</Button>
		</div>
	{:else}
		<div class="space-y-2">
			<FileDrop
				title="Drop a lease photo or PDF here"
				helperText="or click to browse — PDF, JPG, PNG, HEIC accepted"
				onselected={handleFile}
			/>
			<div class="flex flex-wrap items-center gap-x-4 gap-y-1">
				<button type="button" class="text-xs text-muted-foreground underline-offset-4 hover:underline" onclick={reset} data-testid="lease-photo-prefill-back">
					Never mind, I'll type it in
				</button>
				<a href="/scan/new-rental" class="text-xs text-muted-foreground underline-offset-4 hover:underline" data-testid="lease-prefill-full-flow">
					or set up the whole rental from the lease →
				</a>
			</div>
		</div>
	{/if}
</div>
