<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import {
		securityDeposits,
		downloadMoveOutStatement,
		type DeductSecurityDepositRequest,
		type FundSecurityDepositRequest,
		type RefundSecurityDepositRequest,
	} from '$lib/api/endpoints/securityDeposits';
	import { documents, fileObjectUrl } from '$lib/api/endpoints/documents';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatDateOnly } from '$lib/utils/date';
	import { depositDeductionSchema, parseForm } from '$lib/schemas';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { FileText, Image as ImageIcon, Upload } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const depositId = $derived(parseInt(page.params.id ?? '0', 10));
	const today = () => new Date().toISOString().slice(0, 10);

	const depositQuery = createQuery(() => ({
		queryKey: ['deposit', depositId],
		queryFn: () => securityDeposits.get(depositId),
		enabled: depositId > 0,
	}));

	const deposit = $derived(depositQuery.data);

	function invalidateDeposit() {
		queryClient.invalidateQueries({ queryKey: ['deposit', depositId] });
		queryClient.invalidateQueries({ queryKey: ['deposits'] });
	}

	function positiveAmount(value: string): number | null {
		const parsed = Number(value);
		return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
	}

	function stableOperationKey(
		fingerprint: string,
		current: { fingerprint: string; key: string },
	): { fingerprint: string; key: string } {
		return current.fingerprint === fingerprint && current.key
			? current
			: { fingerprint, key: crypto.randomUUID() };
	}

	// Funding records actual money received into the account prepared during move-in.
	let showFundForm = $state(false);
	let fundAmount = $state('');
	let fundEffectiveOn = $state(today());
	let fundDescription = $state('Security deposit received');
	let fundPaymentMethod = $state('');
	let fundReference = $state('');
	let fundErrors = $state<Record<string, string>>({});
	let fundOperation = $state({ fingerprint: '', key: '' });

	function openFund() {
		fundAmount = '';
		fundEffectiveOn = today();
		fundDescription = 'Security deposit received';
		fundPaymentMethod = '';
		fundReference = '';
		fundErrors = {};
		fundOperation = { fingerprint: '', key: '' };
		showFundForm = true;
	}

	const fundMutation = createMutation(() => ({
		mutationFn: ({ operationKey, body }: { operationKey: string; body: FundSecurityDepositRequest }) =>
			securityDeposits.fund(deposit!.tenantAccountId, operationKey, body),
		onSuccess: () => {
			showSuccess('Security deposit funds recorded.');
			showFundForm = false;
			fundOperation = { fingerprint: '', key: '' };
			invalidateDeposit();
		},
		onError: (error) => showError(apiErrorMessage(error)),
	}));

	function submitFund() {
		if (!deposit) return;
		const amount = positiveAmount(fundAmount);
		const errors: Record<string, string> = {};
		if (amount == null) errors.amount = 'Enter an amount greater than zero.';
		if (!fundEffectiveOn) errors.effectiveOn = 'Received date is required.';
		if (!fundDescription.trim()) errors.description = 'Description is required.';
		if (!fundPaymentMethod.trim()) errors.paymentMethodSummary = 'Payment method is required.';
		fundErrors = errors;
		if (Object.keys(errors).length > 0 || amount == null) return;

		const body: FundSecurityDepositRequest = {
			securityDepositAccountId: deposit.id,
			amount,
			effectiveOn: fundEffectiveOn,
			description: fundDescription.trim(),
			paymentMethodSummary: fundPaymentMethod.trim(),
		};
		if (fundReference.trim()) body.externalReference = fundReference.trim();
		const fingerprint = JSON.stringify(body);
		fundOperation = stableOperationKey(fingerprint, fundOperation);
		fundMutation.mutate({ operationKey: fundOperation.key, body });
	}

	// Deductions are append-only deposit and tenant-ledger facts.
	let showDeductionForm = $state(false);
	let deductionReason = $state('');
	let deductionAmount = $state('');
	let deductionNotes = $state('');
	let deductionEffectiveOn = $state(today());
	let deductionErrors = $state<Record<string, string>>({});
	let deductionOperation = $state({ fingerprint: '', key: '' });

	function openDeduction() {
		if (!deposit || deposit.heldBalance <= 0) return;
		deductionReason = '';
		deductionAmount = '';
		deductionNotes = '';
		deductionEffectiveOn = today();
		deductionErrors = {};
		deductionOperation = { fingerprint: '', key: '' };
		showDeductionForm = true;
	}

	const deductionMutation = createMutation(() => ({
		mutationFn: ({ operationKey, body }: { operationKey: string; body: DeductSecurityDepositRequest }) =>
			securityDeposits.addDeduction(deposit!.tenantAccountId, operationKey, body),
		onSuccess: () => {
			showSuccess('Security deposit deduction recorded.');
			showDeductionForm = false;
			deductionOperation = { fingerprint: '', key: '' };
			invalidateDeposit();
		},
		onError: (error) => showError(apiErrorMessage(error)),
	}));

	function submitDeduction() {
		if (!deposit) return;
		const result = parseForm(depositDeductionSchema, {
			reason: deductionReason,
			amount: deductionAmount,
			notes: deductionNotes,
		});
		const errors = result.errors ? { ...result.errors } : {};
		if (!deductionEffectiveOn) errors.effectiveOn = 'Deduction date is required.';
		if (!result.errors && result.data.amount > deposit.heldBalance) {
			errors.amount = 'The deduction cannot exceed the held balance.';
		}
		deductionErrors = errors;
		if (Object.keys(errors).length > 0 || result.errors) return;

		const body: DeductSecurityDepositRequest = {
			securityDepositAccountId: deposit.id,
			amount: result.data.amount,
			effectiveOn: deductionEffectiveOn,
			reason: result.data.reason,
		};
		if (result.data.notes) body.notes = result.data.notes;
		const fingerprint = JSON.stringify(body);
		deductionOperation = stableOperationKey(fingerprint, deductionOperation);
		deductionMutation.mutate({ operationKey: deductionOperation.key, body });
	}

	// A blank refund amount deliberately means "refund the full currently held balance".
	let showRefundForm = $state(false);
	let refundAmount = $state('');
	let refundEffectiveOn = $state(today());
	let refundDescription = $state('Security deposit refund');
	let refundReference = $state('');
	let refundErrors = $state<Record<string, string>>({});
	let refundOperation = $state({ fingerprint: '', key: '' });

	function openRefund() {
		if (!deposit || deposit.heldBalance <= 0) return;
		refundAmount = '';
		refundEffectiveOn = today();
		refundDescription = 'Security deposit refund';
		refundReference = '';
		refundErrors = {};
		refundOperation = { fingerprint: '', key: '' };
		showRefundForm = true;
	}

	const refundMutation = createMutation(() => ({
		mutationFn: ({ operationKey, body }: { operationKey: string; body: RefundSecurityDepositRequest }) =>
			securityDeposits.refund(deposit!.tenantAccountId, operationKey, body),
		onSuccess: () => {
			showSuccess('Security deposit refund recorded.');
			showRefundForm = false;
			refundOperation = { fingerprint: '', key: '' };
			invalidateDeposit();
		},
		onError: (error) => showError(apiErrorMessage(error)),
	}));

	function submitRefund() {
		if (!deposit) return;
		const amount = refundAmount.trim() ? positiveAmount(refundAmount) : undefined;
		const errors: Record<string, string> = {};
		if (refundAmount.trim() && amount == null) errors.amount = 'Enter an amount greater than zero.';
		if (amount != null && amount > deposit.heldBalance) errors.amount = 'The refund cannot exceed the held balance.';
		if (!refundEffectiveOn) errors.effectiveOn = 'Refund date is required.';
		if (!refundDescription.trim()) errors.description = 'Description is required.';
		refundErrors = errors;
		if (Object.keys(errors).length > 0) return;

		const body: RefundSecurityDepositRequest = {
			securityDepositAccountId: deposit.id,
			effectiveOn: refundEffectiveOn,
			description: refundDescription.trim(),
		};
		if (amount != null) body.amount = amount;
		if (refundReference.trim()) body.externalReference = refundReference.trim();
		const fingerprint = JSON.stringify(body);
		refundOperation = stableOperationKey(fingerprint, refundOperation);
		refundMutation.mutate({ operationKey: refundOperation.key, body });
	}

	function money(value: number, currency = 'USD') {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(value ?? 0);
	}

	const depositStatusMap: Record<string, { label?: string; class: string }> = {
		NotFunded: { label: 'Not funded', class: 'm3-tone-chip border m3-tone--warning' },
		Held: { class: 'm3-tone-chip border m3-tone--info' },
		PartiallyReturned: { label: 'Partially returned', class: 'm3-tone-chip border m3-tone--warning' },
		Returned: { class: 'm3-tone-chip border m3-tone--success' },
		Withheld: { class: 'm3-tone-chip border m3-tone--error' },
	};

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

	const ENTITY_TYPE = 'SecurityDepositAccount';
	const photosQuery = createQuery(() => ({
		queryKey: ['deposit-documents', depositId],
		queryFn: () => documents.list(ENTITY_TYPE, depositId),
		enabled: depositId > 0,
	}));
	const photos = $derived(photosQuery.data ?? []);
	let thumbUrls = $state<Record<number, string>>({});
	const loadedUrls: Record<number, string> = {};

	$effect(() => {
		for (const doc of photos) {
			if (!doc.isImage || loadedUrls[doc.id]) continue;
			loadedUrls[doc.id] = '';
			fileObjectUrl(doc.id, { thumb: true }).then((url) => {
				loadedUrls[doc.id] = url;
				thumbUrls = { ...thumbUrls, [doc.id]: url };
			}).catch(() => undefined);
		}
	});
	$effect(() => () => Object.values(loadedUrls).forEach((url) => url && URL.revokeObjectURL(url)));

	let photoInput: HTMLInputElement | undefined = $state();
	let uploadingPhoto = $state(false);
	async function handlePhotoChange(event: Event) {
		const input = event.currentTarget as HTMLInputElement;
		const file = input.files?.[0];
		if (!file) return;
		uploadingPhoto = true;
		try {
			await documents.upload(ENTITY_TYPE, depositId, file, undefined, crypto.randomUUID());
			showSuccess(`"${file.name}" attached.`);
			queryClient.invalidateQueries({ queryKey: ['deposit-documents', depositId] });
		} catch (error) {
			showError(apiErrorMessage(error, 'Upload failed.'));
		} finally {
			uploadingPhoto = false;
			if (photoInput) photoInput.value = '';
		}
	}
</script>

<svelte:head>
	<title>{deposit ? `Deposit – ${deposit.relationshipNumber}` : 'Security Deposit'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="deposit-detail-page">
	{#if depositQuery.isLoading}
		<div class="flex h-48 items-center justify-center"><div class="h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"></div></div>
	{:else if depositQuery.isError || !deposit}
		<div class="flex h-48 flex-col items-center justify-center gap-3 text-center" data-testid="deposit-detail-not-found">
			<p class="text-sm font-medium">Security deposit account not found.</p>
			<Button variant="outline" size="sm" onclick={() => goto('/deposits')}>Back to Deposits</Button>
		</div>
	{:else}
		<PageBreadcrumb crumbs={[{ label: 'Deposits', href: '/deposits' }, { label: deposit.relationshipNumber }]} />

		<div class="my-6 flex flex-wrap items-start gap-3">
			<div class="flex-1">
				<div class="flex flex-wrap items-center gap-2">
					<h1 class="text-2xl font-bold" data-testid="deposit-detail-title">{deposit.propertyName ?? `Property #${deposit.propertyId}`}{deposit.unitNumber ? ` · Unit ${deposit.unitNumber}` : ''}</h1>
					<StatusBadge status={deposit.status} map={depositStatusMap} />
				</div>
				<p class="mt-1 text-sm text-muted-foreground">{deposit.tenantName ?? 'Tenant account'} · {deposit.relationshipNumber}</p>
			</div>
			<div class="flex flex-wrap items-center gap-2">
				<Button size="sm" onclick={openFund} disabled={deposit.status === 'Returned' || deposit.status === 'Withheld' || deposit.status === 'PartiallyReturned'}>Record funds</Button>
				<Button size="sm" variant="outline" onclick={openDeduction} disabled={deposit.heldBalance <= 0}>Add deduction</Button>
				<Button size="sm" variant="outline" onclick={openRefund} disabled={deposit.heldBalance <= 0}>Record refund</Button>
				<Button size="sm" variant="outline" onclick={handleStatementDownload} disabled={downloadingStatement}>
					<FileText class="h-3.5 w-3.5" /> {downloadingStatement ? 'Preparing…' : 'Move-out statement'}
				</Button>
			</div>
		</div>

		<Card.Root class="mb-6">
			<Card.Header><Card.Title class="text-base">Deposit account summary</Card.Title></Card.Header>
			<Card.Content>
				<dl class="grid grid-cols-2 gap-x-6 gap-y-4 sm:grid-cols-3 lg:grid-cols-4">
					<div><dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Held balance</dt><dd class="mt-0.5 font-mono text-sm font-semibold tabular-nums" data-testid="deposit-detail-held-balance">{money(deposit.heldBalance, deposit.currency)}</dd></div>
					<div><dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Total received</dt><dd class="mt-0.5 font-mono text-sm tabular-nums">{money(deposit.totalReceived, deposit.currency)}</dd></div>
					<div><dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Total deductions</dt><dd class="mt-0.5 font-mono text-sm tabular-nums">{money(deposit.totalDeductions, deposit.currency)}</dd></div>
					<div><dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Total refunded</dt><dd class="mt-0.5 font-mono text-sm tabular-nums">{money(deposit.totalRefunded, deposit.currency)}</dd></div>
					<div><dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Account</dt><dd class="mt-0.5 text-sm">{deposit.accountNumber}</dd></div>
					<div><dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Agreement</dt><dd class="mt-0.5 text-sm">#{deposit.originatingAgreementId}</dd></div>
					<div><dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Opened</dt><dd class="mt-0.5 text-sm">{formatDateOnly(deposit.createdAtUtc) || deposit.createdAtUtc}</dd></div>
				</dl>
			</Card.Content>
		</Card.Root>

		<Card.Root data-testid="deposit-photos">
			<Card.Header class="flex flex-row items-center justify-between space-y-0 pb-3">
				<div><Card.Title class="text-base">Move-out photos</Card.Title><p class="mt-1 text-xs text-muted-foreground">Attach condition photos for the move-out statement.</p></div>
				<Button size="sm" variant="outline" onclick={() => photoInput?.click()} disabled={uploadingPhoto}><Upload class="h-3.5 w-3.5" /> {uploadingPhoto ? 'Uploading…' : 'Add photo'}</Button>
				<input bind:this={photoInput} type="file" accept="image/*" class="hidden" onchange={handlePhotoChange} />
			</Card.Header>
			<Card.Content>
				{#if photosQuery.isLoading}<p class="py-4 text-sm text-muted-foreground">Loading photos…</p>
				{:else if photos.length === 0}<p class="py-4 text-sm text-muted-foreground">No photos yet.</p>
				{:else}<ul class="flex flex-wrap gap-3">{#each photos as photo (photo.id)}<li class="w-24"><div class="flex h-24 w-24 items-center justify-center overflow-hidden rounded-md border bg-muted">{#if photo.isImage && thumbUrls[photo.id]}<img src={thumbUrls[photo.id]} alt={photo.fileName} class="h-full w-full object-cover" />{:else if photo.isImage}<ImageIcon class="h-6 w-6 text-muted-foreground" />{:else}<FileText class="h-6 w-6 text-muted-foreground" />{/if}</div><p class="mt-1 truncate text-xs text-muted-foreground">{photo.fileName}</p></li>{/each}</ul>{/if}
			</Card.Content>
		</Card.Root>
	{/if}
</div>

<Dialog.Root open={showFundForm} onOpenChange={(open) => { if (!open) showFundForm = false; }}>
	<Dialog.Content class="max-w-md">
		<Dialog.Header><Dialog.Title>Record deposit funds</Dialog.Title><Dialog.Description>Record money actually received into this existing deposit account.</Dialog.Description></Dialog.Header>
		<div class="space-y-3">
			<div><span class="mb-1 block text-xs text-muted-foreground">Amount</span><Input bind:value={fundAmount} inputmode="decimal" mask="currency" placeholder="0.00" />{#if fundErrors.amount}<p class="mt-1 text-xs text-destructive">{fundErrors.amount}</p>{/if}</div>
			<div><span class="mb-1 block text-xs text-muted-foreground">Received date</span><Input bind:value={fundEffectiveOn} type="date" />{#if fundErrors.effectiveOn}<p class="mt-1 text-xs text-destructive">{fundErrors.effectiveOn}</p>{/if}</div>
			<div><span class="mb-1 block text-xs text-muted-foreground">Payment method</span><Input bind:value={fundPaymentMethod} placeholder="Check, cash, ACH…" />{#if fundErrors.paymentMethodSummary}<p class="mt-1 text-xs text-destructive">{fundErrors.paymentMethodSummary}</p>{/if}</div>
			<div><span class="mb-1 block text-xs text-muted-foreground">Description</span><Input bind:value={fundDescription} />{#if fundErrors.description}<p class="mt-1 text-xs text-destructive">{fundErrors.description}</p>{/if}</div>
			<div><span class="mb-1 block text-xs text-muted-foreground">Reference (optional)</span><Input bind:value={fundReference} placeholder="Check or confirmation number" /></div>
		</div>
		<Dialog.Footer><Button variant="outline" onclick={() => showFundForm = false}>Cancel</Button><Button onclick={submitFund} disabled={fundMutation.isPending}>{fundMutation.isPending ? 'Recording…' : 'Record funds'}</Button></Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<Dialog.Root open={showDeductionForm} onOpenChange={(open) => { if (!open) showDeductionForm = false; }}>
	<Dialog.Content class="max-w-md">
		<Dialog.Header><Dialog.Title>Add deduction</Dialog.Title><Dialog.Description>Apply deposit funds to a documented tenant charge. Held balance: {deposit ? money(deposit.heldBalance, deposit.currency) : ''}.</Dialog.Description></Dialog.Header>
		<div class="space-y-3">
			<div><span class="mb-1 block text-xs text-muted-foreground">Reason</span><Input bind:value={deductionReason} placeholder="Carpet cleaning, broken window…" />{#if deductionErrors.reason}<p class="mt-1 text-xs text-destructive">{deductionErrors.reason}</p>{/if}</div>
			<div><span class="mb-1 block text-xs text-muted-foreground">Amount</span><Input bind:value={deductionAmount} inputmode="decimal" mask="currency" placeholder="0.00" />{#if deductionErrors.amount}<p class="mt-1 text-xs text-destructive">{deductionErrors.amount}</p>{/if}</div>
			<div><span class="mb-1 block text-xs text-muted-foreground">Deduction date</span><Input bind:value={deductionEffectiveOn} type="date" />{#if deductionErrors.effectiveOn}<p class="mt-1 text-xs text-destructive">{deductionErrors.effectiveOn}</p>{/if}</div>
			<div><span class="mb-1 block text-xs text-muted-foreground">Notes (optional)</span><textarea bind:value={deductionNotes} rows="2" class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm"></textarea></div>
		</div>
		<Dialog.Footer><Button variant="outline" onclick={() => showDeductionForm = false}>Cancel</Button><Button onclick={submitDeduction} disabled={deductionMutation.isPending}>{deductionMutation.isPending ? 'Recording…' : 'Add deduction'}</Button></Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<Dialog.Root open={showRefundForm} onOpenChange={(open) => { if (!open) showRefundForm = false; }}>
	<Dialog.Content class="max-w-md">
		<Dialog.Header><Dialog.Title>Record deposit refund</Dialog.Title><Dialog.Description>Leave amount blank to refund the full held balance of {deposit ? money(deposit.heldBalance, deposit.currency) : ''}.</Dialog.Description></Dialog.Header>
		<div class="space-y-3">
			<div><span class="mb-1 block text-xs text-muted-foreground">Amount (optional)</span><Input bind:value={refundAmount} inputmode="decimal" mask="currency" placeholder="Full held balance" />{#if refundErrors.amount}<p class="mt-1 text-xs text-destructive">{refundErrors.amount}</p>{/if}</div>
			<div><span class="mb-1 block text-xs text-muted-foreground">Refund date</span><Input bind:value={refundEffectiveOn} type="date" />{#if refundErrors.effectiveOn}<p class="mt-1 text-xs text-destructive">{refundErrors.effectiveOn}</p>{/if}</div>
			<div><span class="mb-1 block text-xs text-muted-foreground">Description</span><Input bind:value={refundDescription} />{#if refundErrors.description}<p class="mt-1 text-xs text-destructive">{refundErrors.description}</p>{/if}</div>
			<div><span class="mb-1 block text-xs text-muted-foreground">Reference (optional)</span><Input bind:value={refundReference} placeholder="Check or confirmation number" /></div>
		</div>
		<Dialog.Footer><Button variant="outline" onclick={() => showRefundForm = false}>Cancel</Button><Button onclick={submitRefund} disabled={refundMutation.isPending}>{refundMutation.isPending ? 'Recording…' : 'Record refund'}</Button></Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
