<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import {
		securityDeposits,
		downloadMoveOutStatement
	} from '$lib/api/endpoints/securityDeposits';
	import { documents, fileObjectUrl } from '$lib/api/endpoints/documents';
	import type { DocumentItem, SecurityDepositHolding } from '$lib/types';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatDateOnly } from '$lib/utils/date';
	import { depositDeductionSchema, parseForm } from '$lib/schemas';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { AlertTriangle, FileText, Image as ImageIcon, Upload } from '@lucide/svelte';

	const queryClient = useQueryClient();

	const depositId = $derived(parseInt(page.params.id ?? '0', 10));

	const depositQuery = createQuery(() => ({
		queryKey: ['deposit', depositId],
		queryFn: () => securityDeposits.get(depositId),
		enabled: depositId > 0,
	}));

	const deposit = $derived(depositQuery.data);

	// Cumulative deductions can exceed the held amount (the server has no cumulative cap; it only
	// clamps the net refund at $0). Surface that overage so it isn't misread as a $0 refund alone.
	const overDeduction = $derived(
		deposit ? Math.max(0, deposit.totalDeductions - deposit.amount) : 0
	);

	function invalidateDeposit() {
		queryClient.invalidateQueries({ queryKey: ['deposit', depositId] });
		queryClient.invalidateQueries({ queryKey: ['deposits'] });
	}

	// --- Add deduction dialog ---
	let deductionReason = $state('');
	let deductionAmount = $state('');
	let deductionNotes = $state('');
	let deductionErrors = $state<Record<string, string>>({});
	let showDeductionForm = $state(false);

	// Live preview while typing: how much this new deduction would push cumulative deductions
	// past the held amount (0 when it still fits within the deposit).
	const deductionProjectedOver = $derived(
		deposit && deductionAmount.trim() !== '' && Number.isFinite(Number(deductionAmount)) && Number(deductionAmount) > 0
			? Math.max(0, deposit.totalDeductions + Number(deductionAmount) - deposit.amount)
			: 0
	);

	function openDeduction() {
		if (deposit?.status !== 'Held') return;
		deductionReason = '';
		deductionAmount = '';
		deductionNotes = '';
		deductionErrors = {};
		showDeductionForm = true;
	}
	function closeDeduction() {
		deductionErrors = {};
		showDeductionForm = false;
	}

	const deductionMut = createMutation(() => ({
		mutationFn: (body: { reason: string; amount: number; notes?: string }) =>
			securityDeposits.addDeduction(depositId, body),
		onSuccess: () => {
			showSuccess('Deduction added.');
			closeDeduction();
			invalidateDeposit();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitDeduction() {
		const result = parseForm(depositDeductionSchema, {
			reason: deductionReason,
			amount: deductionAmount,
			notes: deductionNotes,
		});
		if (result.errors) {
			deductionErrors = result.errors;
			return;
		}
		deductionErrors = {};
		const { reason, amount, notes } = result.data;
		const body: { reason: string; amount: number; notes?: string } = { reason, amount };
		if (notes) body.notes = notes;
		deductionMut.mutate(body);
	}

	// --- Process return dialog ---
	let returnNotes = $state('');
	let showReturnDialog = $state(false);

	function openReturn() {
		returnNotes = '';
		showReturnDialog = true;
	}
	function closeReturn() {
		showReturnDialog = false;
	}

	const returnMut = createMutation(() => ({
		mutationFn: (body: { notes?: string }) =>
			securityDeposits.processReturn(depositId, body),
		onSuccess: () => {
			showSuccess('Deposit return processed.');
			closeReturn();
			invalidateDeposit();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitReturn() {
		const body: { notes?: string } = {};
		if (returnNotes.trim()) body.notes = returnNotes.trim();
		returnMut.mutate(body);
	}

	// --- Helpers ---
	function money(value: number) {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2 }).format(value ?? 0);
	}

	function formatDate(val: string | undefined): string {
		if (!val) return '—';
		// Date-only fields (held/returned) are stored UTC-midnight; format UTC-pinned
		// so they don't slip back a day. See formatDateOnly.
		return formatDateOnly(val) || val;
	}

	const depositStatusMap: Record<string, { label?: string; class: string }> = {
		Held: { class: 'm3-tone-chip border m3-tone--info' },
		PartiallyReturned: { label: 'Partially Returned', class: 'm3-tone-chip border m3-tone--warning' },
		Returned: { class: 'm3-tone-chip border m3-tone--success' },
		// Whole deposit consumed by deductions — nothing returned. Red, distinct from the amber partial.
		Withheld: { class: 'm3-tone-chip border m3-tone--error' },
	};

	// --- Move-out statement PDF (authed blob download) ---
	let downloadingStatement = $state(false);

	async function handleStatementDownload() {
		downloadingStatement = true;
		try {
			await downloadMoveOutStatement(depositId);
		} catch {
			showError('Could not download the move-out statement. Please try again.');
		} finally {
			downloadingStatement = false;
		}
	}

	// --- Photos (attached as documents with entityType=SecurityDeposit) ---
	const ENTITY_TYPE = 'SecurityDeposit';

	const photosQuery = createQuery(() => ({
		queryKey: ['deposit-documents', depositId],
		queryFn: () => documents.list(ENTITY_TYPE, depositId),
		enabled: depositId > 0,
	}));

	const photos = $derived(photosQuery.data ?? []);

	// Cache of object URLs for image thumbnails, keyed by document id. Kept in a
	// plain (untracked) map alongside the reactive one so the loader effect never
	// re-runs just because a thumbnail finished loading.
	let thumbUrls = $state<Record<number, string>>({});
	const loadedUrls: Record<number, string> = {};

	$effect(() => {
		for (const doc of photos) {
			if (!doc.isImage || loadedUrls[doc.id]) continue;
			loadedUrls[doc.id] = ''; // mark in-flight so we don't double-fetch
			fileObjectUrl(doc.id, { thumb: true })
				.then((url) => {
					loadedUrls[doc.id] = url;
					thumbUrls = { ...thumbUrls, [doc.id]: url };
				})
				.catch(() => {
					// Leave it without a thumbnail; the icon fallback covers it.
				});
		}
	});

	// Revoke all object URLs once when the page tears down.
	$effect(() => {
		return () => {
			for (const url of Object.values(loadedUrls)) {
				if (url) URL.revokeObjectURL(url);
			}
		};
	});

	let photoInput: HTMLInputElement | undefined = $state();
	let uploadingPhoto = $state(false);

	async function handlePhotoChange(e: Event) {
		const input = e.currentTarget as HTMLInputElement;
		const file = input.files?.[0];
		if (!file) return;
		uploadingPhoto = true;
		try {
			await documents.upload(ENTITY_TYPE, depositId, file);
			showSuccess(`"${file.name}" attached.`);
			queryClient.invalidateQueries({ queryKey: ['deposit-documents', depositId] });
		} catch (err) {
			showError(apiErrorMessage(err, 'Upload failed.'));
		} finally {
			uploadingPhoto = false;
			if (photoInput) photoInput.value = '';
		}
	}

	function triggerPhotoUpload() {
		photoInput?.click();
	}
</script>

<svelte:head>
	<title>{deposit ? `Deposit – ${deposit.leaseNumber ?? `Lease #${deposit.leaseId}`}` : 'Security Deposit'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="deposit-detail-page">
	{#if depositQuery.isLoading}
		<div class="flex h-48 items-center justify-center" data-testid="deposit-detail-loading">
			<div class="h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"></div>
		</div>
	{:else if depositQuery.isError || !deposit}
		<div class="flex h-48 flex-col items-center justify-center gap-3 text-center" data-testid="deposit-detail-not-found">
			<p class="text-sm font-medium">Security deposit not found.</p>
			<Button variant="outline" size="sm" onclick={() => goto('/deposits')}>Back to Deposits</Button>
		</div>
	{:else}
		<!-- Breadcrumb -->
		<div class="mb-4">
			<PageBreadcrumb
				crumbs={[
					{ label: 'Deposits', href: '/deposits' },
					{ label: deposit.leaseNumber ?? `Lease #${deposit.leaseId}` },
				]}
			/>
		</div>

		<!-- Header -->
		<div class="mb-6 flex flex-wrap items-start gap-3">
			<div class="flex-1">
				<div class="flex flex-wrap items-center gap-2">
					<h1 class="text-2xl font-bold" data-testid="deposit-detail-title">
						{deposit.leaseNumber ?? `Lease #${deposit.leaseId}`}
					</h1>
					<StatusBadge status={deposit.status} map={depositStatusMap} />
				</div>
				<p class="mt-1 text-sm text-muted-foreground">Security deposit holding</p>
			</div>
			<div class="flex flex-wrap items-center gap-2">
				<Button
					data-testid="deposit-add-deduction"
					variant="outline"
					size="sm"
					onclick={openDeduction}
					disabled={deposit.status !== 'Held'}
				>
					Add deduction
				</Button>
				<Button
					data-testid="deposit-process-return"
					variant="outline"
					size="sm"
					onclick={openReturn}
					disabled={deposit.status !== 'Held'}
				>
					Process Return
				</Button>
				<Button
					data-testid="deposit-download-statement"
					variant="outline"
					size="sm"
					onclick={handleStatementDownload}
					disabled={downloadingStatement}
				>
					<FileText class="h-3.5 w-3.5" />
					{downloadingStatement ? 'Preparing…' : 'Download move-out statement (PDF)'}
				</Button>
			</div>
		</div>

		<!-- Summary card -->
		<Card.Root class="mb-6">
			<Card.Header>
				<Card.Title class="text-base">Holding Summary</Card.Title>
			</Card.Header>
			<Card.Content>
				<dl class="grid grid-cols-2 gap-x-6 gap-y-4 sm:grid-cols-3 lg:grid-cols-4">
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Amount Held</dt>
						<dd class="mt-0.5 font-mono text-sm font-semibold tabular-nums" data-testid="deposit-detail-amount">{money(deposit.amount)}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Total Deductions</dt>
						<dd class="mt-0.5 font-mono text-sm tabular-nums" data-testid="deposit-detail-deductions">{money(deposit.totalDeductions)}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Net Refund</dt>
						<dd class="mt-0.5 font-mono text-sm font-semibold tabular-nums" data-testid="deposit-detail-net-refund">{money(deposit.netRefund)}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Held Since</dt>
						<dd class="mt-0.5 text-sm" data-testid="deposit-detail-held-at">{formatDate(deposit.heldAt)}</dd>
					</div>
					{#if deposit.returnedAt}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Returned On</dt>
							<dd class="mt-0.5 text-sm">{formatDate(deposit.returnedAt)}</dd>
						</div>
					{/if}
					{#if deposit.returnedAmount != null}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Returned Amount</dt>
							<dd class="mt-0.5 font-mono text-sm tabular-nums">{money(deposit.returnedAmount)}</dd>
						</div>
					{/if}
					{#if deposit.notes}
						<div class="col-span-2 sm:col-span-3 lg:col-span-4">
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Notes</dt>
							<dd class="mt-0.5 text-sm text-muted-foreground">{deposit.notes}</dd>
						</div>
					{/if}
				</dl>
			</Card.Content>
		</Card.Root>

		<!-- Over-deduction notice: deductions have exceeded the deposit held, so the net refund is
		     clamped to $0 and the remainder is owed by the tenant (not auto-collected here). -->
		{#if overDeduction > 0}
			<div
				class="m3-warning-surface mb-6 flex items-start gap-2 rounded-lg px-4 py-3 text-sm"
				data-testid="deposit-over-deduction-warning"
			>
				<AlertTriangle class="mt-0.5 h-4 w-4 shrink-0" />
				<p>
					Deductions exceed the deposit held by
					<span class="font-semibold">{money(overDeduction)}</span> — the net refund is
					{money(0)} and the tenant will owe the difference.
				</p>
			</div>
		{/if}

		<!-- Photos — attach move-out condition photos so the statement can include them -->
		<Card.Root class="mb-6" data-testid="deposit-photos">
			<Card.Header class="flex flex-row items-center justify-between space-y-0 pb-3">
				<div>
					<Card.Title class="text-base">Photos</Card.Title>
					<p class="mt-1 text-xs text-muted-foreground">
						Attach move-out condition photos. They're included on the move-out statement.
					</p>
				</div>
				<Button
					size="sm"
					variant="outline"
					class="gap-1.5"
					onclick={triggerPhotoUpload}
					disabled={uploadingPhoto}
					data-testid="deposit-photo-upload"
				>
					<Upload class="h-3.5 w-3.5" />
					{uploadingPhoto ? 'Uploading…' : 'Add photo'}
				</Button>
				<input
					bind:this={photoInput}
					type="file"
					accept="image/*"
					class="hidden"
					onchange={handlePhotoChange}
					data-testid="deposit-photo-input"
				/>
			</Card.Header>
			<Card.Content class="pt-0">
				{#if photosQuery.isLoading}
					<p class="py-4 text-sm text-muted-foreground" data-testid="deposit-photos-loading">Loading photos…</p>
				{:else if photos.length === 0}
					<p class="py-4 text-sm text-muted-foreground" data-testid="deposit-photos-empty">
						No photos yet. Add a photo to document the unit's condition.
					</p>
				{:else}
					<ul class="flex flex-wrap gap-3" data-testid="deposit-photos-list">
						{#each photos as photo (photo.id)}
							<li class="w-24" data-testid="deposit-photo-{photo.id}">
								<div class="flex h-24 w-24 items-center justify-center overflow-hidden rounded-md border border-border bg-muted">
									{#if photo.isImage && thumbUrls[photo.id]}
										<img src={thumbUrls[photo.id]} alt={photo.fileName} class="h-full w-full object-cover" />
									{:else if photo.isImage}
										<ImageIcon class="h-6 w-6 text-muted-foreground" />
									{:else}
										<FileText class="h-6 w-6 text-muted-foreground" />
									{/if}
								</div>
								<p class="mt-1 truncate text-xs text-muted-foreground" title={photo.fileName}>{photo.fileName}</p>
							</li>
						{/each}
					</ul>
				{/if}
			</Card.Content>
		</Card.Root>

		<!-- Deductions section -->
		<div class="mb-2">
			<h2 class="text-lg font-semibold">Deductions</h2>
		</div>
		{#if deposit.deductions.length === 0}
			<p class="rounded-lg border border-dashed py-8 text-center text-sm text-muted-foreground" data-testid="deposit-no-deductions">
				No deductions recorded for this deposit.
			</p>
		{:else}
			<Card.Root data-testid="deposit-deductions-list">
				<Card.Content class="p-0">
					<table class="w-full text-sm">
						<thead>
							<tr class="border-b">
								<th class="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">Reason</th>
								<th class="px-4 py-3 text-right text-xs font-medium uppercase tracking-wide text-muted-foreground">Amount</th>
								<th class="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-muted-foreground hidden sm:table-cell">Notes</th>
							</tr>
						</thead>
						<tbody>
							{#each deposit.deductions as deduction, i}
								<tr class="border-b last:border-0" data-testid="deduction-row-{i}">
									<td class="px-4 py-3 font-medium">{deduction.reason}</td>
									<td class="px-4 py-3 text-right font-mono tabular-nums">{money(deduction.amount)}</td>
									<td class="px-4 py-3 text-muted-foreground hidden sm:table-cell">{deduction.notes ?? '—'}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</Card.Content>
			</Card.Root>
		{/if}
	{/if}
</div>

<!-- Add Deduction Dialog -->
<Dialog.Root open={showDeductionForm} onOpenChange={(v) => { if (!v) closeDeduction(); }}>
	<Dialog.Content class="max-w-md">
		<Dialog.Header>
			<Dialog.Title>Add deduction</Dialog.Title>
			{#if deposit}
				<Dialog.Description>
					Deduct from the {money(deposit.amount)} deposit on lease {deposit.leaseNumber ?? deposit.leaseId}.
				</Dialog.Description>
			{/if}
		</Dialog.Header>
		<div class="space-y-3" data-testid="deduction-form">
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Reason</span>
				<Input
					data-testid="deduction-reason-input"
					bind:value={deductionReason}
					placeholder="e.g. Carpet cleaning, Broken window"
				/>
				{#if deductionErrors.reason}<p class="mt-1 text-xs text-destructive" data-testid="deduction-reason-error">{deductionErrors.reason}</p>{/if}
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Amount</span>
				<Input
					data-testid="deduction-amount-input"
					bind:value={deductionAmount}
					type="text"
					inputmode="decimal"
					mask="currency"
					placeholder="0.00"
				/>
				{#if deductionErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="deduction-amount-error">{deductionErrors.amount}</p>{/if}
				{#if deductionProjectedOver > 0}
					<div
						class="m3-warning-surface mt-2 flex items-start gap-2 rounded-md px-2.5 py-1.5 text-xs"
						data-testid="deduction-exceeds-warning"
					>
						<AlertTriangle class="mt-0.5 h-3.5 w-3.5 shrink-0" />
						<span>
							Deductions exceed the deposit held — the tenant will owe the difference
							({money(deductionProjectedOver)} over).
						</span>
					</div>
				{/if}
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Notes (optional)</span>
				<textarea
					data-testid="deduction-notes-input"
					bind:value={deductionNotes}
					rows="2"
					class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
				></textarea>
			</div>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={closeDeduction} data-testid="deduction-cancel">Cancel</Button>
			<Button onclick={submitDeduction} disabled={deductionMut.isPending} data-testid="deduction-save">
				{deductionMut.isPending ? 'Saving…' : 'Add deduction'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<!-- Process Return Confirm Dialog -->
<ConfirmDialog
	open={showReturnDialog}
	title="Process deposit return"
	message={deposit
		? `Return ${money(deposit.netRefund)} (deposit ${money(deposit.amount)} minus ${money(deposit.totalDeductions)} in deductions) to the tenant?`
		: ''}
	busy={returnMut.isPending}
	testid="return-confirm"
	confirmLabel="Process return"
	onconfirm={submitReturn}
	oncancel={closeReturn}
/>
